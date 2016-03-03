﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using IOFile = System.IO.File;

namespace Serilog.Sinks.Seq
{
    class HttpLogShipper : IDisposable
    {
        readonly string _apiKey;
        readonly int _batchPostingLimit;
#if !TIMER
        readonly PortableTimer _timer;
#else
        readonly Timer _timer;
#endif
        readonly TimeSpan _period;
        readonly long? _eventBodyLimitBytes;
        readonly object _stateLock = new object();

        // As per SeqSink
        LoggingLevelSwitch _levelControlSwitch;
        static readonly TimeSpan RequiredLevelCheckInterval = TimeSpan.FromMinutes(2);
        DateTime _nextRequiredLevelCheckUtc = DateTime.UtcNow.Add(RequiredLevelCheckInterval);

        volatile bool _unloading;
        readonly string _bookmarkFilename;
        readonly string _logFolder;
        readonly HttpClient _httpClient;
        readonly string _candidateSearchPath;

        const string ApiKeyHeaderName = "X-Seq-ApiKey";
        const string BulkUploadResource = "api/events/raw";

        public HttpLogShipper(string serverUrl, string bufferBaseFilename, string apiKey, int batchPostingLimit, TimeSpan period, 
            long? eventBodyLimitBytes, LoggingLevelSwitch levelControlSwitch)
        {
            _apiKey = apiKey;
            _batchPostingLimit = batchPostingLimit;
            _period = period;
            _eventBodyLimitBytes = eventBodyLimitBytes;
            _levelControlSwitch = levelControlSwitch;

            var baseUri = serverUrl;
            if (!baseUri.EndsWith("/"))
                baseUri += "/";

            _httpClient = new HttpClient { BaseAddress = new Uri(baseUri) };

            _bookmarkFilename = Path.GetFullPath(bufferBaseFilename + ".bookmark");
            _logFolder = Path.GetDirectoryName(_bookmarkFilename);
            _candidateSearchPath = Path.GetFileName(bufferBaseFilename) + "*.json";
            _period = period;

#if !TIMER
            _timer = new PortableTimer(c => OnTick());
#else
            _timer = new Timer(s => OnTick());
#endif

#if APPDOMAIN
            AppDomain.CurrentDomain.DomainUnload += OnAppDomainUnloading;
            AppDomain.CurrentDomain.ProcessExit += OnAppDomainUnloading;
#endif

            SetTimer();
        }

        void OnAppDomainUnloading(object sender, EventArgs args)
        {
            CloseAndFlush();
        }

        void CloseAndFlush()
        {
            lock (_stateLock)
            {
                if (_unloading)
                    return;

                _unloading = true;
            }

#if APPDOMAIN
            AppDomain.CurrentDomain.DomainUnload -= OnAppDomainUnloading;
            AppDomain.CurrentDomain.ProcessExit -= OnAppDomainUnloading;
#endif

#if !TIMER
            _timer.Dispose();
#else
            var wh = new ManualResetEvent(false);
            if (_timer.Dispose(wh))
                wh.WaitOne();
#endif

            OnTick();
        }

        /// <summary>
        /// Get the last "minimum level" indicated by the Seq server, if any.
        /// </summary>
        public LogEventLevel? MinimumAcceptedLevel
        {
            get
            {
                lock (_stateLock)
                    return _levelControlSwitch?.MinimumLevel;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Free resources held by the sink.
        /// </summary>
        /// <param name="disposing">If true, called because the object is being disposed; if false,
        /// the object is being disposed from the finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            CloseAndFlush();
        }

        void SetTimer()
        {
            // Note, called under _stateLock

#if !TIMER
            _timer.Start(_period);
#else
            _timer.Change(_period, Timeout.InfiniteTimeSpan);
#endif
        }

        void OnTick()
        {
            LogEventLevel? minimumAcceptedLevel = null;

            try
            {
                int count;
                do
                {
                    count = 0;

                    // Locking the bookmark ensures that though there may be multiple instances of this
                    // class running, only one will ship logs at a time.

                    using (var bookmark = IOFile.Open(_bookmarkFilename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        long nextLineBeginsAtOffset;
                        string currentFile;

                        TryReadBookmark(bookmark, out nextLineBeginsAtOffset, out currentFile);

                        var fileSet = GetFileSet();

                        if (currentFile == null || !IOFile.Exists(currentFile))
                        {
                            nextLineBeginsAtOffset = 0;
                            currentFile = fileSet.FirstOrDefault();
                        }

                        if (currentFile == null)
                            continue;

                        var payload = new StringWriter();
                        payload.Write("{\"Events\":[");
                        var delimStart = "";

                        using (var current = IOFile.Open(currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            current.Position = nextLineBeginsAtOffset;

                            string nextLine;
                            while (count < _batchPostingLimit &&
                                   TryReadLine(current, ref nextLineBeginsAtOffset, out nextLine))
                            {
                                // Count is the indicator that work was done, so advances even in the (rare) case an
                                // oversized event is dropped.
                                ++count;

                                if (_eventBodyLimitBytes.HasValue && Encoding.UTF8.GetByteCount(nextLine) > _eventBodyLimitBytes.Value)
                                {
                                    SelfLog.WriteLine("Event JSON representation exceeds the byte size limit of {0} and will be dropped; data: {1}", _eventBodyLimitBytes, nextLine);
                                }
                                else
                                {
                                    payload.Write(delimStart);
                                    payload.Write(nextLine);
                                    delimStart = ",";
                                }
                            }

                            payload.Write("]}");
                        }

                        if (count > 0 || _levelControlSwitch != null && _nextRequiredLevelCheckUtc < DateTime.UtcNow)
                        {
                            lock (_stateLock)
                            {
                                _nextRequiredLevelCheckUtc = DateTime.UtcNow.Add(RequiredLevelCheckInterval);
                            }

                            var payloadText = payload.ToString();
                            var content = new StringContent(payloadText, Encoding.UTF8, "application/json");
                            if (!string.IsNullOrWhiteSpace(_apiKey))
                                content.Headers.Add(ApiKeyHeaderName, _apiKey);

                            var result = _httpClient.PostAsync(BulkUploadResource, content).Result;
                            if (result.IsSuccessStatusCode)
                            {
                                WriteBookmark(bookmark, nextLineBeginsAtOffset, currentFile);
                                var returned = result.Content.ReadAsStringAsync().Result;
                                minimumAcceptedLevel = SeqApi.ReadEventInputResult(returned);
                            }
                            else if (result.StatusCode == HttpStatusCode.BadRequest ||
                                     result.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                            {
                                var invalidPayloadFilename = $"invalid-{result.StatusCode}-{Guid.NewGuid():n}.json";
                                var invalidPayloadFile = Path.Combine(_logFolder, invalidPayloadFilename);
                                SelfLog.WriteLine("HTTP shipping failed with {0}: {1}; dumping payload to {2}", result.StatusCode, result.Content.ReadAsStringAsync().Result, invalidPayloadFile);
                                IOFile.WriteAllText(invalidPayloadFile, payloadText);
                                WriteBookmark(bookmark, nextLineBeginsAtOffset, currentFile);
                            }
                            else
                            {
                                SelfLog.WriteLine("Received failed HTTP shipping result {0}: {1}", result.StatusCode, result.Content.ReadAsStringAsync().Result);
                            }
                        }
                        else
                        {
                            // Only advance the bookmark if no other process has the
                            // current file locked, and its length is as we found it.
                                
                            if (fileSet.Length == 2 && fileSet.First() == currentFile && IsUnlockedAtLength(currentFile, nextLineBeginsAtOffset))
                            {
                                WriteBookmark(bookmark, 0, fileSet[1]);
                            }

                            if (fileSet.Length > 2)
                            {
                                // Once there's a third file waiting to ship, we do our
                                // best to move on, though a lock on the current file
                                // will delay this.

                                IOFile.Delete(fileSet[0]);
                            }
                        }
                    }
                }
                while (count == _batchPostingLimit);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Exception while emitting periodic batch from {0}: {1}", this, ex);
            }
            finally
            {
                lock (_stateLock)
                {
                    if (minimumAcceptedLevel == null)
                    {
                        if (_levelControlSwitch != null)
                            _levelControlSwitch.MinimumLevel = LevelAlias.Minimum;
                    }
                    else
                    {
                        if (_levelControlSwitch == null)
                            _levelControlSwitch = new LoggingLevelSwitch(minimumAcceptedLevel.Value);
                        else
                            _levelControlSwitch.MinimumLevel = minimumAcceptedLevel.Value;
                    }

                    if (!_unloading)
                        SetTimer();
                }
            }
        }

        bool IsUnlockedAtLength(string file, long maxLen)
        {
            try
            {
                using (var fileStream = IOFile.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    return fileStream.Length <= maxLen;
                }
            }
            catch (IOException ex)
            {
                var errorCode = Marshal.GetHRForException(ex) & ((1 << 16) - 1);
                if (errorCode != 32 && errorCode != 33)
                {
                    SelfLog.WriteLine("Unexpected I/O exception while testing locked status of {0}: {1}", file, ex);
                }
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Unexpected exception while testing locked status of {0}: {1}", file, ex);
            }

            return false;
        }

        static void WriteBookmark(FileStream bookmark, long nextLineBeginsAtOffset, string currentFile)
        {
            using (var writer = new StreamWriter(bookmark))
            {
                writer.WriteLine("{0}:::{1}", nextLineBeginsAtOffset, currentFile);
            }
        }

        // It would be ideal to chomp whitespace here, but not required.
        static bool TryReadLine(Stream current, ref long nextStart, out string nextLine)
        {
            var includesBom = nextStart == 0;

            if (current.Length <= nextStart)
            {
                nextLine = null;
                return false;
            }

            current.Position = nextStart;

            // Important not to dispose this StreamReader as the stream must remain open.
            var reader = new StreamReader(current, Encoding.UTF8, false, 128);
            nextLine = reader.ReadLine();
 
            if (nextLine == null)
                return false;

            nextStart += Encoding.UTF8.GetByteCount(nextLine) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (includesBom)
                nextStart += 3;

            return true;
        }

        static void TryReadBookmark(Stream bookmark, out long nextLineBeginsAtOffset, out string currentFile)
        {
            nextLineBeginsAtOffset = 0;
            currentFile = null;

            if (bookmark.Length != 0)
            {
                // Important not to dispose this StreamReader as the stream must remain open.
                var reader = new StreamReader(bookmark, Encoding.UTF8, false, 128);
                var current = reader.ReadLine();

                if (current != null)
                {
                    bookmark.Position = 0;
                    var parts = current.Split(new[] { ":::" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        nextLineBeginsAtOffset = long.Parse(parts[0]);
                        currentFile = parts[1];
                    }
                }
                
            }
        }

        string[] GetFileSet()
        {
            return Directory.GetFiles(_logFolder, _candidateSearchPath)
                .OrderBy(n => n)
                .ToArray();
        }
    }
}