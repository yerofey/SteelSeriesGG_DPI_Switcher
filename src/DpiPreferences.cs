using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;

namespace AeroxDpi
{
    public static class DpiPreferences
    {
        public static string SettingsPath
        {
            get
            {
                // Retain the original data folder so app renames preserve saved selections.
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Aerox DPI", "last-dpi.txt");
            }
        }

        public static int? Load()
        {
            return Load(SettingsPath);
        }

        internal static int? Load(string path)
        {
            try
            {
                int dpi;
                string value = File.ReadAllText(path).Trim();
                if (Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out dpi)
                    && IsSupported(dpi))
                    return dpi;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return null;
        }

        public static void Save(int dpi)
        {
            Save(dpi, SettingsPath);
        }

        internal static void Save(int dpi, string path)
        {
            if (!IsSupported(dpi))
                throw new ArgumentOutOfRangeException("dpi", dpi, "Unsupported DPI value.");

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, ".last-dpi-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] contents = Encoding.UTF8.GetBytes(dpi.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }

                if (File.Exists(fullPath))
                    File.Replace(temporaryPath, fullPath, null);
                else
                    File.Move(temporaryPath, fullPath);
            }
            finally
            {
                // A failed replacement must leave the previous setting untouched.
                // Cleanup errors must not hide the original write/replace error.
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
            }
        }

        private static bool IsSupported(int dpi)
        {
            return dpi == 700 || dpi == 800 || dpi == 900 || dpi == 1000 || dpi == 1100;
        }
    }
}
