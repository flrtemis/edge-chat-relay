using System;
using System.IO;
using System.Text.Json;

namespace EdgeChatRelay
{
    public class AppSettings
    {
        public string Url1 { get; set; } = "about:blank";
        public string Url2 { get; set; } = "about:blank";
        public bool Relay1 { get; set; } = true;
        public bool Relay2 { get; set; } = true;
        public bool Log1 { get; set; } = true;
        public bool Log2 { get; set; } = true;
        public string LogPath1 { get; set; } = "";
        public string LogPath2 { get; set; } = "";
        public string SendMode1 { get; set; } = "auto";
        public string SendMode2 { get; set; } = "auto";
        public int DelayMs { get; set; } = 2500;
        public int MaxTurns { get; set; } = 50;
        public int Width { get; set; } = 1440;
        public int Height { get; set; } = 920;
        public int Splitter { get; set; } = 720;

        public static string ConfigDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EdgeChatRelay");
            }
        }

        public static string DefaultLogDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "EdgeChatRelay");
            }
        }

        private static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
