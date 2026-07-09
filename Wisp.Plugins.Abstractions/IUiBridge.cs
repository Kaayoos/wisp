using System;
using System.Threading.Tasks;
using Wisp.Plugins.UI;

namespace Wisp.Plugins
{
    /// <summary>
    /// Marshals work onto Wisp's UI (dispatcher) thread. You MUST create or touch any WPF object -
    /// windows, controls, bitmaps - on this thread. Event handlers run on a background thread, so use
    /// this to hop over before showing your overlay/UI.
    /// </summary>
    public interface IUiBridge
    {
        /// <summary>True if the caller is already on the UI thread.</summary>
        bool IsUiThread { get; }

        /// <summary>Posts <paramref name="action"/> to the UI thread and returns immediately (fire-and-forget).</summary>
        void RunOnUiThread(Action action);

        /// <summary>Runs <paramref name="action"/> on the UI thread and awaits its completion.</summary>
        Task InvokeAsync(Action action);

        /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result.</summary>
        Task<T> InvokeAsync<T>(Func<T> func);

        /// <summary>
        /// Raw access to the host's main window for advanced WPF reflection/manipulation.
        /// Warning: Use cautiously, as this bypasses all API stability guarantees!
        /// </summary>
        object MainWindow { get; }

        /// <summary>
        /// Injects a new tab into the host's sidebar and wires it to display the provided content.
        /// </summary>
        /// <param name="id">Unique identifier for the tab</param>
        /// <param name="title">Display text in the sidebar</param>
        /// <param name="iconHex">A Segoe MDL2 Assets icon: the glyph character itself ("") or a hex
        /// code ("E10B", "0xE10B", "&amp;#xE10B;"). Wisp normalises any of these to the right glyph.</param>
        /// <param name="content">The WPF element to display when the tab is active</param>
        void AddSidebarTab(string id, string title, string iconHex, object content);

        /// <summary>
        /// Injects a new settings block into the host's Settings tab.
        /// </summary>
        void AddSettingsCategory(string title, object content);

        /// <summary>
        /// Shows a custom modal dialog containing your provided WPF element.
        /// </summary>
        void ShowCustomDialog(string title, object content, double width = 400, double height = 400);

        /// <summary>
        /// Shows a native Wisp toast (the same transient popup the app uses for "Clip saved" etc.), so
        /// your plugin's feedback looks built-in. Safe to call from any thread.
        /// </summary>
        /// <param name="title">Bold first line.</param>
        /// <param name="message">Optional second line; null for a one-liner.</param>
        /// <param name="kind">Severity → icon + colour.</param>
        void ShowToast(string title, string? message = null, ToastKind kind = ToastKind.Info);

        /// <summary>
        /// Adds (or replaces, by <see cref="ClipAction.Id"/>) a command to the right-click menu of every
        /// clip in the library. Call in <see cref="IWispPlugin.OnEnabled"/>. Wisp removes your actions
        /// automatically when the plugin is disabled.
        /// </summary>
        void RegisterClipAction(ClipAction action);

        /// <summary>Removes a clip action you registered.</summary>
        void UnregisterClipAction(string actionId);

        /// <summary>
        /// Adds (or replaces, by id) an item to Wisp's system-tray context menu, above Quit - for quick
        /// actions that don't need the main window open. <paramref name="onClick"/> runs on the UI thread.
        /// Wisp removes your tray items automatically when the plugin is disabled.
        /// </summary>
        void AddTrayMenuItem(string id, string label, Action onClick);

        /// <summary>Removes a tray menu item you added.</summary>
        void RemoveTrayMenuItem(string id);
    }
}
