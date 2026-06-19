using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Per-save JSON storage factory for SDK consumer mods.
/// </summary>
public sealed class SdkSaveStore
{
    private SdkLogging _log;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Creates a mod-scoped save store using the supplied JSON file name or a default based on the mod ID.
    /// </summary>
    public ModSaveStore ForMod(string modId, string fileName = null)
    {
        return new ModSaveStore(_log, modId, fileName);
    }

    /// <summary>
    /// JSON storage helper rooted at <c>BepInEx\saves\{saveName}</c>.
    /// </summary>
    public sealed class ModSaveStore
    {
        private readonly SdkLogging _log;
        private readonly string _modId;
        private readonly string _fileName;

        internal ModSaveStore(SdkLogging log, string modId, string fileName)
        {
            _log = log;
            _modId = string.IsNullOrWhiteSpace(modId) ? "mod" : modId;
            _fileName = string.IsNullOrWhiteSpace(fileName) ? $"{_modId}.json" : fileName;
        }

        /// <summary>
        /// Returns the full path for the mod save file, creating the save directory if needed.
        /// </summary>
        public string GetPath(string saveName)
        {
            var safeSaveName = NormalizeSaveName(saveName);
            var dir = Path.Combine(Application.dataPath, "..", "BepInEx", "saves", safeSaveName);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, _fileName);
        }

        /// <summary>
        /// Returns true when the mod save file exists for the save name.
        /// </summary>
        public bool Exists(string saveName) => File.Exists(GetPath(saveName));

        /// <summary>
        /// Loads JSON data for the save, returning the supplied default when the file is missing or corrupt.
        /// Corrupt files are backed up before returning.
        /// </summary>
        public T Load<T>(string saveName, T defaultValue = default)
        {
            var path = GetPath(saveName);
            if (!File.Exists(path))
            {
                _log?.Verbose("sdk.save", $"load mod={_modId} file={_fileName} save={saveName} result=missing");
                return defaultValue;
            }

            try
            {
                var json = File.ReadAllText(path);
                _log?.Verbose("sdk.save", $"load mod={_modId} file={_fileName} save={saveName} result=ok bytes={json.Length}");
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                TryBackupCorruptFile(path);
                _log?.Error($"SaveStore load failed for {_modId}: {ex}");
                return defaultValue;
            }
        }

        /// <summary>
        /// Writes JSON data for the save.
        /// </summary>
        public void Save<T>(string saveName, T data)
        {
            var path = GetPath(saveName);
            try
            {
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(path, json);
                _log?.Verbose("sdk.save", $"save mod={_modId} file={_fileName} save={saveName} result=ok bytes={json.Length}");
            }
            catch (Exception ex)
            {
                _log?.Error($"SaveStore save failed for {_modId}: {ex}");
            }
        }

        /// <summary>
        /// Imports a legacy JSON file once when the new SDK save file does not already exist.
        /// </summary>
        public bool TryImportLegacy<T>(string saveName, string legacyFileName, out T data)
        {
            data = default;
            var safeSaveName = NormalizeSaveName(saveName);
            var dir = Path.Combine(Application.dataPath, "..", "BepInEx", "saves", safeSaveName);
            var legacyPath = Path.Combine(dir, legacyFileName);
            if (File.Exists(GetPath(saveName)) || !File.Exists(legacyPath))
                return false;

            try
            {
                data = JsonConvert.DeserializeObject<T>(File.ReadAllText(legacyPath));
                _log?.Verbose("sdk.save", $"import mod={_modId} legacy={legacyFileName} save={saveName} result=ok");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"Legacy import failed for {_modId}: {ex}");
                return false;
            }
        }

        private static string NormalizeSaveName(string saveName)
        {
            if (string.IsNullOrWhiteSpace(saveName))
                return "_unknown";

            foreach (var c in Path.GetInvalidFileNameChars())
                saveName = saveName.Replace(c, '_');
            return saveName;
        }

        private void TryBackupCorruptFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                var backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(path, backup, overwrite: false);
                _log?.Warning("sdk.save", $"corrupt mod={_modId} file={_fileName} backup={backup}");
            }
            catch (Exception ex)
            {
                _log?.Warning($"Could not back up corrupt save file {path}: {ex.Message}");
            }
        }
    }
}
