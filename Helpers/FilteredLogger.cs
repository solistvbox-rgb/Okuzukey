using System;
using System.Text;
using MediaBrowser.Model.Logging;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Wraps an <see cref="ILogger"/> Emby injects into a provider, suppressing calls below the
    /// severity configured in <see cref="Configuration.PluginConfiguration.LoggingLevel"/>.
    /// </summary>
    /// <remarks>
    /// On a large library this plugin's own log lines can add several MB to
    /// <c>embyserver.txt</c> - almost entirely per-item <c>Debug</c>/<c>Info</c>
    /// calls ("no NFO file found", "metadata successfully read from ...", one line per local
    /// provider - <c>NfoBookProvider</c>/<c>OpfBookProvider</c>/<c>ComicInfoProvider</c>/
    /// <c>LocalImageBookProvider</c>/<c>EmbeddedCoverImageProvider</c>, plus <c>KomgaProvider</c>
    /// if enabled - per item) that are only actionable while actively troubleshooting, not during
    /// normal operation.
    /// <para>
    /// Every provider/API handler wraps its injected logger with <see cref="Wrap(ILogger)"/> once,
    /// at construction, instead of every individual call site checking the configured level
    /// itself.
    /// </para>
    /// <para>
    /// <c>"None"</c>: nothing is logged, not even errors. <c>"ErrorsOnly"</c>: only
    /// <c>Error</c>/<c>ErrorException</c>/<c>Fatal</c>/<c>FatalException</c> pass through.
    /// <c>"Normal"</c> (default): those plus <c>Warn</c> pass through; <c>Debug</c>/<c>Info</c>
    /// (the bulk of the per-item spam) are suppressed. <c>"Verbose"</c>: everything passes
    /// through.
    /// </para>
    /// </remarks>
    internal sealed class FilteredLogger : ILogger
    {
        private readonly ILogger _inner;

        private FilteredLogger(ILogger inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// Wraps <paramref name="inner"/>, or returns <c>null</c> unchanged if it is <c>null</c>
        /// (e.g. <c>Request?.Logger</c> in an API handler) so callers can keep using <c>?.</c>.
        /// </summary>
        public static ILogger Wrap(ILogger inner)
        {
            return inner == null ? null : new FilteredLogger(inner);
        }

        /// <summary>
        /// The minimum <see cref="LogSeverity"/> rank (see <see cref="GetRank"/>) that currently
        /// passes through, based on <see cref="Configuration.PluginConfiguration.LoggingLevel"/>.
        /// An unrecognized value is treated the same as the default, <c>"Normal"</c>.
        /// </summary>
        private static int GetMinimumRank()
        {
            switch (Plugin.Instance?.Configuration?.LoggingLevel)
            {
                case "None":
                    return int.MaxValue;
                case "ErrorsOnly":
                    return GetRank(LogSeverity.Error);
                case "Verbose":
                    return int.MinValue;
                case "Normal":
                default:
                    return GetRank(LogSeverity.Warn);
            }
        }

        private static int GetRank(LogSeverity severity)
        {
            switch (severity)
            {
                case LogSeverity.Debug: return 0;
                case LogSeverity.Info: return 1;
                case LogSeverity.Warn: return 2;
                case LogSeverity.Error: return 3;
                case LogSeverity.Fatal: return 4;
                default: return 2;
            }
        }

        private static bool Allows(LogSeverity severity)
        {
            return GetRank(severity) >= GetMinimumRank();
        }

        /// <inheritdoc />
        public void Debug(string message, params object[] paramList)
        {
            if (Allows(LogSeverity.Debug))
            {
                _inner.Debug(message, paramList);
            }
        }

        /// <inheritdoc />
        public void Debug(ReadOnlyMemory<char> message)
        {
            if (Allows(LogSeverity.Debug))
            {
                _inner.Debug(message.ToString());
            }
        }

        /// <inheritdoc />
        public void Info(string message, params object[] paramList)
        {
            if (Allows(LogSeverity.Info))
            {
                _inner.Info(message, paramList);
            }
        }

        /// <inheritdoc />
        public void Info(ReadOnlyMemory<char> message)
        {
            if (Allows(LogSeverity.Info))
            {
                _inner.Info(message.ToString());
            }
        }

        /// <inheritdoc />
        public void Warn(string message, params object[] paramList)
        {
            if (Allows(LogSeverity.Warn))
            {
                _inner.Warn(message, paramList);
            }
        }

        /// <inheritdoc />
        public void Warn(ReadOnlyMemory<char> message)
        {
            if (Allows(LogSeverity.Warn))
            {
                _inner.Warn(message.ToString());
            }
        }

        /// <inheritdoc />
        public void Error(string message, params object[] paramList)
        {
            if (Allows(LogSeverity.Error))
            {
                _inner.Error(message, paramList);
            }
        }

        /// <inheritdoc cref="Error(string, object[])"/>
        public void Error(ReadOnlyMemory<char> message)
        {
            if (Allows(LogSeverity.Error))
            {
                _inner.Error(message.ToString());
            }
        }

        /// <inheritdoc cref="Error(string, object[])"/>
        /// <remarks>
        /// If the exception chain carries an OS-localized message (e.g. a German socket error
        /// text), the full exception is not handed to Emby's error report, since that would print
        /// the localized text; instead the English description from <see cref="ExceptionText"/>,
        /// the exception type and the stack trace are logged.
        /// </remarks>
        public void ErrorException(string message, Exception exception, params object[] paramList)
        {
            if (!Allows(LogSeverity.Error))
            {
                return;
            }

            if (exception == null || !ExceptionText.ContainsOsLocalizedMessage(exception))
            {
                _inner.ErrorException(message, exception, paramList);
                return;
            }

            var details = " " + exception.GetType().FullName + ": " + ExceptionText.Describe(exception)
                          + Environment.NewLine + exception.StackTrace;
            _inner.Error(message + EscapeFormat(details), paramList);
        }

        /// <summary>Escapes braces so appended text is not parsed as a <c>string.Format</c> placeholder.</summary>
        private static string EscapeFormat(string text)
        {
            return text.Replace("{", "{{").Replace("}", "}}");
        }

        /// <inheritdoc />
        public void Fatal(string message, params object[] paramList)
        {
            if (Allows(LogSeverity.Fatal))
            {
                _inner.Fatal(message, paramList);
            }
        }

        /// <inheritdoc cref="Fatal(string, object[])"/>
        public void FatalException(string message, Exception exception, params object[] paramList)
        {
            if (Allows(LogSeverity.Fatal))
            {
                _inner.FatalException(message, exception, paramList);
            }
        }

        /// <inheritdoc />
        public void Log(LogSeverity severity, string message, params object[] paramList)
        {
            if (Allows(severity))
            {
                _inner.Log(severity, message, paramList);
            }
        }

        /// <inheritdoc />
        public void Log(LogSeverity severity, ReadOnlyMemory<char> message)
        {
            if (Allows(severity))
            {
                _inner.Log(severity, message.ToString());
            }
        }

        /// <inheritdoc />
        public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent)
        {
            if (Allows(severity))
            {
                _inner.LogMultiline(message, severity, additionalContent);
            }
        }
    }
}
