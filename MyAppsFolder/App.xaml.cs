using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace MyAppsFolder
{
    public partial class App : Application
    {
        // --- Settings you can change ---
        // Where the shortcuts live. Kept out of the way in AppData so it doesn't
        // clutter the Desktop; reach it from the folder button in the popup.
        public static readonly string FolderPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyApps");
        public const string FolderTitle = "My Apps";
        // --------------------------------

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Directory.CreateDirectory(FolderPath);
            MigrateLegacyDesktopFolder();

            // If one or more shortcuts were dragged onto this .exe (or its desktop
            // icon), Windows launches it with those file paths as arguments instead
            // of opening normally. Detect that and just add them to the folder.
            if (e.Args.Length > 0)
            {
                int added = AddDroppedItems(e.Args, FolderPath);
                MessageBox.Show(
                    added > 0 ? $"Added {added} app(s) to {FolderTitle}" : "Couldn't add that item",
                    FolderTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Nothing to show yet: point the user at the folder and exit *before*
            // creating the window, so no empty popup flashes on screen. Retried a
            // few times because a virus scanner or sync client can briefly lock a
            // folder right after files are moved into it.
            bool hasItems = false;
            for (int attempt = 0; attempt < 4 && !hasItems; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(400);
                hasItems = HasAnyItems(FolderPath);
            }

            if (!hasItems)
            {
                MessageBox.Show(
                    $"This folder is empty.\n\nDrag some app shortcuts into:\n{FolderPath}\n\n" +
                    "Tip: make subfolders in there (e.g. \"Games\", \"Work\") and each becomes a tab.",
                    FolderTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var window = new MainWindow(FolderPath, FolderTitle);
            window.Show();
        }

        // Earlier versions kept the shortcuts in "Desktop\My Apps". If that folder
        // is still around (an old build recreated it, a sync client restored it,
        // etc.) pull anything useful out of it and delete it, so it stops
        // reappearing on the Desktop.
        private static void MigrateLegacyDesktopFolder()
        {
            try
            {
                string legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "My Apps");

                if (!Directory.Exists(legacy) ||
                    string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(FolderPath), StringComparison.OrdinalIgnoreCase))
                    return;

                MergeInto(legacy, FolderPath);

                // Remove it if it's now empty (leave it alone if something is still there).
                if (Directory.GetFileSystemEntries(legacy).Length == 0)
                    Directory.Delete(legacy, true);
            }
            catch
            {
                // Self-healing only - never block startup over it.
            }
        }

        private static void MergeInto(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(source))
            {
                string target = Path.Combine(dest, Path.GetFileName(file));
                try
                {
                    if (!File.Exists(target)) File.Move(file, target);
                    else File.Delete(file); // already have it - drop the duplicate
                }
                catch { }
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                MergeInto(dir, Path.Combine(dest, Path.GetFileName(dir)));
                try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }
                catch { }
            }
        }

        // Real files in the folder, minus hidden/system bookkeeping files like
        // desktop.ini or thumbs.db. Shared by the startup check and the window.
        public static string[] GetLaunchableItems(string folderPath)
        {
            if (!Directory.Exists(folderPath))
                return Array.Empty<string>();

            var result = new List<string>();
            foreach (var file in Directory.GetFiles(folderPath))
            {
                try
                {
                    var attr = File.GetAttributes(file);
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;
                }
                catch
                {
                    // If attributes can't be read, still show the item.
                }
                result.Add(file);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result.ToArray();
        }

        // True if the folder itself, or any immediate subfolder, holds a shortcut.
        private static bool HasAnyItems(string folderPath)
        {
            if (GetLaunchableItems(folderPath).Length > 0)
                return true;

            try
            {
                foreach (var dir in Directory.GetDirectories(folderPath))
                    if (GetLaunchableItems(dir).Length > 0)
                        return true;
            }
            catch
            {
                // Ignore unreadable subfolders.
            }

            return false;
        }

        // Copies dropped shortcuts (.lnk/.url) into the folder as-is. For a raw
        // .exe or other file, creates a shortcut to it instead of moving/copying
        // the actual program.
        private static int AddDroppedItems(string[] paths, string folderPath)
        {
            Directory.CreateDirectory(folderPath);
            int count = 0;

            foreach (var p in paths)
            {
                try
                {
                    string ext = Path.GetExtension(p).ToLowerInvariant();

                    if (ext == ".lnk" || ext == ".url")
                    {
                        string dest = Path.Combine(folderPath, Path.GetFileName(p));
                        if (!File.Exists(dest))
                        {
                            File.Copy(p, dest);
                            count++;
                        }
                    }
                    else if (File.Exists(p))
                    {
                        string dest = Path.Combine(folderPath, Path.GetFileNameWithoutExtension(p) + ".lnk");
                        if (!File.Exists(dest))
                        {
                            CreateShortcut(p, dest);
                            count++;
                        }
                    }
                }
                catch
                {
                    // Skip anything that fails and keep processing the rest.
                }
            }

            return count;
        }

        private static void CreateShortcut(string targetPath, string shortcutPath)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.Save();
        }
    }
}
