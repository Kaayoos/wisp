using System;

namespace Wisp.Plugins
{
    /// <summary>
    /// Writes to Wisp's shared log (<c>%AppData%\Wisp\wisp.log</c>). Every line is automatically
    /// prefixed with your plugin id, so your messages are easy to find and attribute.
    /// </summary>
    public interface IWispLog
    {
        /// <summary>Informational message.</summary>
        void Info(string message);

        /// <summary>Warning - something unexpected but recoverable.</summary>
        void Warn(string message);

        /// <summary>Error, with an optional exception whose details are appended.</summary>
        void Error(string message, Exception? ex = null);
    }
}
