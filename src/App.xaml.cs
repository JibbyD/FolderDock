// ─────────────────────────────────────────────────────────────────────────────
//  App.xaml.cs
//
//  The program's entry point. WPF creates one App object when the .exe starts
//  and calls OnStartup(). This file decides what to do before any window opens:
//    • work out where the shortcut folder is
//    • if shortcuts were dropped onto the .exe, file them away and quit
//    • if there's nothing to show, say so and quit
//    • otherwise open the popup (MainWindow)
//  It also holds the small helpers the window reuses (listing a folder,
//  reading / writing the hand-picked ".order" file).
// ─────────────────────────────────────────────────────────────────────────────

using System;                       // Environment, Activator, StringComparer…
using System.Collections.Generic;   // List<>, Dictionary<>, IEnumerable<>
using System.IO;                    // Directory, File, Path, FileAttributes
using System.Windows;               // Application, MessageBox, StartupEventArgs

namespace FolderDock
{
    /// <summary>
    /// The WPF application object. Runs once at launch, before any window.
    /// (The matching App.xaml just names this class and sets ShutdownMode.)
    /// </summary>
    public partial class App : Application
    {
        // ── Settings you can change ──────────────────────────────────────────

        /// <summary>
        /// Folder the popup reads shortcuts from. Lives in %APPDATA%\MyApps so it
        /// doesn't sit on the Desktop; open it from the folder button in the popup.
        /// </summary>
        public static readonly string FolderPath =
            Path.Combine(                                             // join the two pieces with a "\"
                Environment.GetFolderPath(                            // ask Windows for a known folder…
                    Environment.SpecialFolder.ApplicationData),       // …the roaming AppData folder
                "MyApps");                                            // our subfolder inside it

        /// <summary>Label for the first tab (loose shortcuts) and message-box titles.</summary>
        public const string FolderTitle = "My Apps";

        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// WPF calls this once when the app starts. <paramref name="e"/>.Args holds
        /// any file paths Windows passed in (e.g. shortcuts dropped on the .exe).
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);                       // let WPF do its own startup first

            Directory.CreateDirectory(FolderPath);   // make the folder if it's the first ever run (no-op if it exists)
            MigrateLegacyDesktopFolder();            // move shortcuts out of an old "Desktop\My Apps" if one is lying around

            // ── Case 1: files were dropped onto the .exe ────────────────────
            // Windows launches "MyAppsFolder.exe C:\path\a.lnk C:\path\b.url"
            // when you drag shortcuts onto it, so Args is non-empty.
            if (e.Args.Length > 0)
            {
                int added = AddDroppedItems(e.Args, FolderPath);   // copy them into the folder, count how many stuck
                MessageBox.Show(                                   // brief confirmation, then quit (don't open the popup)
                    added > 0
                        ? $"Added {added} app(s) to {FolderTitle}"
                        : "Couldn't add that item",
                    FolderTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();                                        // end the app
                return;                                            // stop here
            }

            // ── Case 2: is there anything to show? ─────────────────────────
            // Try a few times: right after files are moved into a folder a
            // sync client or antivirus can briefly lock it, making it look empty.
            bool hasItems = false;
            for (int attempt = 0; attempt < 4 && !hasItems; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(400);  // wait 400 ms between retries (not before the first)
                hasItems = HasAnyItems(FolderPath);                   // check the folder + its immediate subfolders
            }

            if (!hasItems)                                             // still nothing after all retries
            {
                MessageBox.Show(                                       // tell the user where to put shortcuts, then quit
                    $"This folder is empty.\n\nDrag some app shortcuts into:\n{FolderPath}\n\n" +
                    "Tip: make subfolders in there (e.g. \"Games\", \"Work\") and each becomes a tab.",
                    FolderTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // ── Case 3: normal launch ─────────────────────────────────────
            var window = new MainWindow(FolderPath, FolderTitle);      // build the popup (its constructor loads the tabs)
            window.Show();                                             // show it; the app now runs until the window closes
        }

        /// <summary>
        /// Older builds kept shortcuts in "Desktop\My Apps". If that folder is
        /// still around (an old build recreated it, a sync client restored it…),
        /// move its contents into the current folder and delete it so it stops
        /// reappearing on the Desktop.
        /// </summary>
        private static void MigrateLegacyDesktopFolder()
        {
            try
            {
                string legacy = Path.Combine(                                   // the old location: Desktop\My Apps
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "My Apps");

                if (!Directory.Exists(legacy) ||                                // nothing to migrate, or…
                    string.Equals(Path.GetFullPath(legacy),                     // …the old and new paths are actually the
                                  Path.GetFullPath(FolderPath),                 //    same folder (someone pointed FolderPath
                                  StringComparison.OrdinalIgnoreCase))          //    back at the Desktop) — don't self-merge
                    return;

                MergeInto(legacy, FolderPath);                                  // copy files/subfolders across

                if (Directory.GetFileSystemEntries(legacy).Length == 0)        // if the old folder is now completely empty…
                    Directory.Delete(legacy, true);                             // …remove it
            }
            catch
            {
                // This is a convenience only — never let it stop the app starting.
            }
        }

        /// <summary>
        /// Moves every file from <paramref name="source"/> into <paramref name="dest"/>,
        /// recursing into subfolders. A name that already exists in dest wins and the
        /// source copy is dropped. Empty source subfolders are deleted as we go.
        /// </summary>
        private static void MergeInto(string source, string dest)
        {
            Directory.CreateDirectory(dest);                        // make sure the destination exists

            foreach (var file in Directory.GetFiles(source))        // every file directly in source
            {
                string target = Path.Combine(dest, Path.GetFileName(file));   // same name, under dest
                try
                {
                    if (!File.Exists(target)) File.Move(file, target);         // not there yet → move it over
                    else File.Delete(file);                                    // already have it → bin the duplicate
                }
                catch { }                                                     // locked / permission → skip this one
            }

            foreach (var dir in Directory.GetDirectories(source))   // every subfolder in source
            {
                MergeInto(dir, Path.Combine(dest, Path.GetFileName(dir)));            // merge it into the matching dest subfolder
                try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }  // tidy up if now empty
                catch { }
            }
        }

        /// <summary>Name of the hidden file that stores a tab's hand-picked icon order.</summary>
        public const string OrderFileName = ".order";

        /// <summary>
        /// Lists the shortcut files directly inside <paramref name="folderPath"/>,
        /// skipping hidden/system junk (desktop.ini, thumbs.db, our own .order),
        /// then puts them in the order saved for that folder (see ApplySavedOrder).
        /// Used by both the startup "is it empty?" check and the window.
        /// </summary>
        public static string[] GetLaunchableItems(string folderPath)
        {
            if (!Directory.Exists(folderPath))          // folder gone? →
                return Array.Empty<string>();           // return an empty list, not null

            var result = new List<string>();            // full paths we'll show
            foreach (var file in Directory.GetFiles(folderPath))
            {
                if (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal))
                    continue;                            // ".order" and any other dotfile — not an app

                try
                {
                    var attr = File.GetAttributes(file);                          // read the file's flags
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;                                                 // hidden or system → skip (bookkeeping file)
                }
                catch
                {
                    // Couldn't read the attributes — show it anyway rather than hide it.
                }

                result.Add(file);                        // keep it
            }

            ApplySavedOrder(folderPath, result);         // reorder the list in place to match .order
            return result.ToArray();
        }

        /// <summary>
        /// Reorders <paramref name="files"/> (a list of full paths) to match the
        /// folder's ".order" file. Names listed there come first, in that order;
        /// anything not listed follows in alphabetical order — so a shortcut you
        /// add after arranging a tab simply appears at the end.
        /// </summary>
        private static void ApplySavedOrder(string folderPath, List<string> files)
        {
            // filename → position (0, 1, 2 …) as read from .order
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string orderPath = Path.Combine(folderPath, OrderFileName);
                if (File.Exists(orderPath))
                {
                    int i = 0;
                    foreach (var line in File.ReadAllLines(orderPath))   // one filename per line
                    {
                        var name = line.Trim();                          // ignore stray whitespace
                        if (name.Length > 0 && !rank.ContainsKey(name))  // skip blanks and duplicates
                            rank[name] = i++;                            // record its position, then bump the counter
                    }
                }
            }
            catch
            {
                // Unreadable .order → rank stays empty → everything sorts alphabetically below.
            }

            int unlisted = rank.Count;   // position used for any file NOT named in .order (i.e. "after all listed ones")

            files.Sort((a, b) =>                                         // custom comparison for List.Sort
            {
                string na = Path.GetFileName(a), nb = Path.GetFileName(b);       // compare by filename, not full path
                int ra = rank.TryGetValue(na, out var xa) ? xa : unlisted;       // a's rank, or "unlisted" if absent
                int rb = rank.TryGetValue(nb, out var xb) ? xb : unlisted;       // b's rank
                return ra != rb
                    ? ra.CompareTo(rb)                                           // different ranks → lower rank first
                    : string.Compare(na, nb, StringComparison.OrdinalIgnoreCase); // same rank → break the tie alphabetically
            });
        }

        /// <summary>
        /// Saves a tab's chosen order. <paramref name="fileNames"/> is the leaf
        /// names (e.g. "Discord.lnk") in the order to remember. Written as a
        /// hidden file so it doesn't show up as a tile or in Explorer.
        /// </summary>
        public static void SaveOrder(string folderPath, IEnumerable<string> fileNames)
        {
            try
            {
                string orderPath = Path.Combine(folderPath, OrderFileName);
                if (File.Exists(orderPath))
                    File.SetAttributes(orderPath, FileAttributes.Normal);   // Windows won't let you overwrite a Hidden file
                File.WriteAllLines(orderPath, fileNames);                   // write one name per line (UTF-8)
                File.SetAttributes(orderPath, FileAttributes.Hidden);       // hide it again
            }
            catch
            {
                // Best effort — e.g. a read-only folder just won't remember the order.
            }
        }

        /// <summary>
        /// True if there's at least one shortcut to show: either loose in
        /// <paramref name="folderPath"/> or inside one of its immediate subfolders
        /// (each of which becomes a tab).
        /// </summary>
        private static bool HasAnyItems(string folderPath)
        {
            if (GetLaunchableItems(folderPath).Length > 0)   // loose shortcuts in the root?
                return true;

            try
            {
                foreach (var dir in Directory.GetDirectories(folderPath))   // each subfolder…
                    if (GetLaunchableItems(dir).Length > 0)                 // …has a shortcut?
                        return true;
            }
            catch
            {
                // A subfolder we can't read — ignore it.
            }

            return false;   // genuinely nothing anywhere
        }

        /// <summary>
        /// Handles the "dragged onto the .exe" case. .lnk / .url files are copied
        /// straight in; anything else gets a new .lnk shortcut pointing at it
        /// (so we never move or copy the actual program). Returns how many were added.
        /// </summary>
        private static int AddDroppedItems(string[] paths, string folderPath)
        {
            Directory.CreateDirectory(folderPath);
            int count = 0;

            foreach (var p in paths)                       // each path Windows handed us
            {
                try
                {
                    string ext = Path.GetExtension(p).ToLowerInvariant();   // ".lnk", ".url", ".exe", …

                    if (ext == ".lnk" || ext == ".url")                     // already a shortcut →
                    {
                        string dest = Path.Combine(folderPath, Path.GetFileName(p));   // same name, in our folder
                        if (!File.Exists(dest))                                        // don't clobber an existing one
                        {
                            File.Copy(p, dest);                                        // copy it in
                            count++;
                        }
                    }
                    else if (File.Exists(p))                                // a real file (program, document…) →
                    {
                        string dest = Path.Combine(folderPath,
                            Path.GetFileNameWithoutExtension(p) + ".lnk");  // make "<name>.lnk"
                        if (!File.Exists(dest))
                        {
                            CreateShortcut(p, dest);                        // build a shortcut that points at p
                            count++;
                        }
                    }
                }
                catch
                {
                    // One bad path shouldn't stop the rest — skip and continue.
                }
            }

            return count;
        }

        /// <summary>
        /// Creates a .lnk at <paramref name="shortcutPath"/> pointing at
        /// <paramref name="targetPath"/>, via the Windows Script Host COM object
        /// (the same thing VBScript uses). 'dynamic' = "resolve these calls at
        /// run time" because there's no compile-time type for the COM object.
        /// </summary>
        private static void CreateShortcut(string targetPath, string shortcutPath)
        {
            dynamic shell = Activator.CreateInstance(                       // create the "WScript.Shell" COM object
                Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);         // make an empty shortcut at that path
            shortcut.TargetPath = targetPath;                              // point it at the target
            shortcut.Save();                                               // write it to disk
        }
    }
}
