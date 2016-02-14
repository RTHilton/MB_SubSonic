﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml;

namespace MusicBeePlugin
{
    public static class Subsonic
    {
        private const int TagCount = 10;
        public static string Host = "localhost";
        public static string Port = "80";
        public static string BasePath = "/";
        public static string Username = "admin";
        public static string Password = "";
        public static bool Transcode;
        public static bool IsInitialized;
        public static string SettingsUrl;
        public static string CacheUrl;
        public static Plugin.MB_SendNotificationDelegate SendNotificationsHandler;
        private static string _serverName;
        private static Exception _lastEx;
        private static object _cacheFileLock;
        private static object _cacheLock;
        private static KeyValuePair<byte, string>[][] _cachedFiles;
        private static Thread _retrieveThread;
        private static string[] _collectionNames;
        private static Dictionary<string, ulong> _lastModified;
        private static object _folderLookupLock;
        private static Dictionary<string, string> _folderLookup;

        public static bool Initialize()
        {
            _lastEx = null;
            try
            {
                if (File.Exists(SettingsUrl))
                {
                    using (var reader = new StreamReader(SettingsUrl))
                    {
                        Host = reader.ReadLine();
                        Port = reader.ReadLine();
                        BasePath = reader.ReadLine();
                        Username = reader.ReadLine();
                        Password = reader.ReadLine();
                        Transcode = reader.ReadLine() == "Y";
                    }
                }
                IsInitialized = SetServerName();
            }
            catch (Exception ex)
            {
                _lastEx = ex;
                IsInitialized = false;
            }
            return IsInitialized;
        }

        private static bool SetServerName()
        {
            bool isPingOk;
            string xml;
            try
            {
                var httpRequest =
                    (HttpWebRequest) WebRequest.Create("http://" + Host + ":" + Port + BasePath);
                httpRequest.Method = "GET";
                httpRequest.Timeout = 5000;
                var responseHost = ((HttpWebResponse) httpRequest.GetResponse()).ResponseUri.Host;
                _serverName = "http://" + responseHost + ":" + Port + BasePath;
                xml = GetHttpRequestXml("ping.view", null, 5000);
                isPingOk = xml.IndexOf(@"status=""ok""", StringComparison.Ordinal) != -1;
                if (isPingOk)
                {
                    return true;
                }
                if (string.Compare(responseHost, Host, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    _lastEx = new IOException(GetErrorMessage(xml));
                    return false;
                }
            }
            catch (Exception ex)
            {
                _lastEx = ex;
            }
            try
            {
                _serverName = "http://" + Host + ":" + Port + BasePath;
                xml = GetHttpRequestXml("ping.view", null, 5000);
                isPingOk = xml.IndexOf(@"status=""ok""", StringComparison.Ordinal) != -1;
                if (!isPingOk)
                {
                    _lastEx = new IOException(GetErrorMessage(xml));
                }
                return isPingOk;
            }
            catch (Exception ex)
            {
                _lastEx = ex;
                return false;
            }
        }

        public static void Close()
        {
            if (_retrieveThread != null && _retrieveThread.IsAlive)
            {
                _retrieveThread.Abort();
                _retrieveThread = null;
            }
        }

        public static bool SetHost(string host, string port, string basePath, string username, string password,
            bool transcode)
        {
            _lastEx = null;
            try
            {
                host = host.Trim();
                if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    host = host.Substring(7);
                }
                port = port.Trim();
                basePath = basePath.Trim();
                if (!basePath.EndsWith(@"/"))
                {
                    basePath += @"/";
                }
                var isChanged = !host.Equals(Host) ||
                                !port.Equals(Port) ||
                                !basePath.Equals(BasePath) ||
                                !username.Equals(Username) ||
                                !password.Equals(Password);
                if (isChanged)
                {
                    var savedHost = Host;
                    var savedPort = Port;
                    var savedBasePath = BasePath;
                    var savedUsername = Username;
                    var savedPassword = Password;
                    bool isPingOk;
                    try
                    {
                        Host = host;
                        Port = port;
                        BasePath = basePath;
                        Username = username;
                        Password = password;
                        isPingOk = SetServerName();
                    }
                    catch (Exception)
                    {
                        isPingOk = false;
                    }
                    if (!isPingOk)
                    {
                        Host = savedHost;
                        Port = savedPort;
                        BasePath = savedBasePath;
                        Username = savedUsername;
                        Password = savedPassword;
                        return false;
                    }
                    IsInitialized = true;
                }
                isChanged = isChanged || Transcode;
                if (!isChanged)
                {
                    return true;
                }
                using (var writer = new StreamWriter(SettingsUrl))
                {
                    writer.WriteAsync(host);
                    writer.WriteLine(port);
                    writer.WriteLine(basePath);
                    writer.WriteLine(username);
                    writer.WriteLine(password);
                    writer.WriteLine(transcode ? "Y" : "N");
                }
                Transcode = transcode;
                try
                {
                    SendNotificationsHandler.Invoke(Plugin.CallbackType.SettingsUpdated);
                }
                catch (Exception ex)
                {
                    _lastEx = ex;
                }
                return true;
            }
            catch (Exception ex)
            {
                _lastEx = ex;
                return false;
            }
        }

        private static string GetErrorMessage(string xml)
        {
            var startIndex = xml.IndexOf("message=", StringComparison.Ordinal);
            if (startIndex.Equals(-1))
            {
                return "Unknown error";
            }
            var endIndex = xml.IndexOf(@"""/>", startIndex, StringComparison.Ordinal);
            return endIndex.Equals(-1)
                ? xml.Substring(startIndex + 9)
                : xml.Substring(startIndex + 9, endIndex - startIndex - 10);
        }

        public static void Refresh()
        {
        }

        public static bool FolderExists(string path)
        {
            var exists = string.IsNullOrEmpty(path) ||
                         path.Equals(@"\") ||
                         GetFolderId(path) != null;
            return exists;
        }

        public static string[] GetFolders(string path)
        {
            _lastEx = null;
            string[] folders;
            if (!IsInitialized)
            {
                folders = new string[] {};
            }
            else if (string.IsNullOrEmpty(path))
            {
                folders = GetRootFolders(true, true, false).Select(folder => folder.Value).ToArray();
            }
            else if (path.IndexOf(@"\", StringComparison.Ordinal)
                .Equals(path.LastIndexOf(@"\", StringComparison.Ordinal)))
            {
                var list = new List<string>();
                var folderId = GetFolderId(path);
                if (folderId != null)
                {
                    var isDirty = false;
                    list.AddRange(GetRootFolders(folderId, path.Substring(0, path.Length - 1), false, false, ref isDirty).Select(folder => folder.Key));
                }
                folders = list.ToArray();
            }
            else
            {
                if (!path.EndsWith(@"\"))
                {
                    path += @"\";
                }
                var folderId = GetFolderId(path);
                if (string.IsNullOrEmpty(folderId))
                {
                    folders = new string[] {};
                }
                else
                {
                    using (var stream = GetHttpRequestStream("getMusicDirectory.view", "id=" + folderId))
                    using (var xmlReader = new XmlTextReader(stream))
                    {
                        var list = new List<string>();
                        while (xmlReader.Read())
                        {
                            if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                                string.Compare(xmlReader.Name, "child", StringComparison.Ordinal).Equals(0))
                            {
                                if (
                                    string.Compare(xmlReader.GetAttribute("isDir"), "true", StringComparison.Ordinal)
                                        .Equals(0))
                                {
                                    folderId = xmlReader.GetAttribute("id");
                                    var folderName = path + xmlReader.GetAttribute("title");
                                    list.Add(folderName);
                                    if (!_folderLookup.ContainsKey(folderName))
                                    {
                                        _folderLookup.Add(folderName, folderId);
                                    }
                                }
                            }
                        }
                        xmlReader.Close();
                        folders = list.ToArray();
                    }
                }
            }
            return folders;
        }

        public static KeyValuePair<byte, string>[][] GetFiles(string path)
        {
            var threadStarted = false;
            _lastEx = null;
            KeyValuePair<byte, string>[][] files;
            if (!IsInitialized)
            {
                files = new KeyValuePair<byte, string>[][] {};
            }
            else
            {
                var cacheLoaded = _cachedFiles != null;
                if (!cacheLoaded && !File.Exists(CacheUrl))
                {
                    files = null;
                }
                else
                {
                    files = GetCachedFiles();
                }
                var cacheUpdating = _retrieveThread != null;
                if (!cacheUpdating && (string.IsNullOrEmpty(path) || !cacheLoaded))
                {
                    threadStarted = true;
                    _retrieveThread = new Thread(ExecuteGetFolderFiles) {IsBackground = true};
                    _retrieveThread.Start();
                }
                if (!string.IsNullOrEmpty(path))
                {
                    if (!cacheLoaded || cacheUpdating || files == null)
                    {
                        return GetFolderFiles(path);
                    }
                    files = GetPathFilteredFiles(files, path);
                }
            }
            if (!threadStarted)
            {
                try
                {
                    SendNotificationsHandler.Invoke(Plugin.CallbackType.FilesRetrievedNoChange);
                }
                catch (Exception ex)
                {
                    _lastEx = ex;
                }
            }
            return files;
        }

        private static KeyValuePair<byte, string>[][] GetCachedFiles()
        {
            if (_cachedFiles == null)
            {
                KeyValuePair<byte, string>[][] files;
                lock (_cacheFileLock)
                {
                    using (
                        var stream = new FileStream(CacheUrl, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                            FileOptions.SequentialScan))
                    using (var reader = new BinaryReader(stream))
                    {
                        var version = reader.ReadInt32();
                        var count = reader.ReadInt32();
                        files = new KeyValuePair<byte, string>[count - 1][];
                        for (var index = 0; index < count - 1; index++)
                        {
                            var tags = new KeyValuePair<byte, string>[TagCount];
                            for (var tagIndex = 0; tagIndex < TagCount; tagIndex++)
                            {
                                var tagType = reader.ReadByte();
                                tags[tagIndex] = new KeyValuePair<byte, string>(tagType, reader.ReadString());
                            }
                            files[index] = tags;
                        }
                        if (version.Equals(2))
                        {
                            count = reader.ReadInt32();
                            for (var index = 1; index < count; index++)
                            {
                                var collectionName = reader.ReadString();
                                if (!_lastModified.ContainsKey(collectionName))
                                {
                                    _lastModified.Add(collectionName, reader.ReadUInt64());
                                }
                            }
                        }
                        reader.Close();
                    }
                }
                lock (_cacheLock)
                {
                    if (_cachedFiles == null)
                    {
                        _cachedFiles = files;
                    }
                }
            }
            return _cachedFiles;
        }

        private static KeyValuePair<byte, string>[][] GetPathFilteredFiles(KeyValuePair<byte, string>[][] files,
            string path)
        {
            var filteredFiles = new List<KeyValuePair<byte, string>[]>();
            if (!path.EndsWith(@"\"))
            {
                path += @"\";
            }
            for (var index = 0; index < files.Length - 1; index++)
            {
                if (files[index][0].Value.StartsWith(path))
                {
                    filteredFiles.Add(files[index]);
                }
            }
            files = filteredFiles.ToArray();
            Array.Sort(files, new FileSorter());
            return files;
        }

        private static void ExecuteGetFolderFiles()
        {
            try
            {
                var list = new List<KeyValuePair<byte, string>[]>();
                var folders = GetRootFolders(false, true, true);
                if (folders != null)
                {
                    foreach (var folder in folders)
                    {
                        GetFolderFiles(folder.Value, folder.Key, list);
                    }
                    var files = list.ToArray();
                    KeyValuePair<byte, string>[][] oldCachedFiles;
                    lock (_cacheLock)
                    {
                        oldCachedFiles = _cachedFiles;
                        _cachedFiles = files;
                    }
                    var anyChanges = oldCachedFiles == null || _cachedFiles.Length != oldCachedFiles.Length;
                    if (!anyChanges)
                    {
                        for (var index = 0; index < _cachedFiles.Length - 1; index++)
                        {
                            var tags1 = _cachedFiles[index];
                            var tags2 = oldCachedFiles[index];
                            for (var tagIndex = 0; tagIndex < TagCount - 1; tagIndex++)
                            {
                                if (
                                    string.Compare(tags1[tagIndex].Value, tags2[tagIndex].Value,
                                        StringComparison.Ordinal) != 0)
                                {
                                    anyChanges = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (!anyChanges)
                    {
                        try
                        {
                            SendNotificationsHandler.Invoke(Plugin.CallbackType.FilesRetrievedNoChange);
                        }
                        catch (Exception ex)
                        {
                            _lastEx = ex;
                        }
                    }
                    else
                    {
                        try
                        {
                            SendNotificationsHandler.Invoke(Plugin.CallbackType.FilesRetrievedChanged);
                        }
                        catch (Exception ex)
                        {
                            _lastEx = ex;
                        }
                        try
                        {
                            lock (_cacheFileLock)
                            {
                                using (
                                    var stream = new FileStream(CacheUrl, FileMode.Create, FileAccess.Write,
                                        FileShare.None))
                                using (var writer = new BinaryWriter(stream))
                                {
                                    writer.Write(2); // version
                                    writer.Write(files.Length);
                                    for (var index = 0; index < files.Length - 1; index++)
                                    {
                                        var tags = files[index];
                                        for (var tagIndex = 0; tagIndex < TagCount; tagIndex++)
                                        {
                                            var tag = tags[tagIndex];
                                            writer.Write(tag.Key);
                                            writer.Write(tag.Value);
                                        }
                                    }
                                    writer.Write(_lastModified.Count);
                                    foreach (var item in _lastModified)
                                    {
                                        writer.Write(item.Key);
                                        writer.Write(item.Value);
                                    }
                                    writer.Close();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _lastEx = ex;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastEx = ex;
                try
                {
                    SendNotificationsHandler.Invoke(Plugin.CallbackType.FilesRetrievedFail);
                }
                catch
                {
                    _lastEx = ex;
                }
            }
            finally
            {
                _retrieveThread = null;
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> GetRootFolders(bool collectionOnly, bool refresh,
            bool dirtyOnly)
        {
            var folders = new List<KeyValuePair<string, string>>();
            lock (_folderLookupLock)
            {
                if (refresh || _folderLookup.Count.Equals(0))
                {
                    folders = new List<KeyValuePair<string, string>>();
                    var collection = new List<KeyValuePair<string, string>>();
                    using (var stream = GetHttpRequestStream("getMusicFolders.view", null))
                    using (var xmlReader = new XmlTextReader(stream))
                    {
                        while (xmlReader.Read())
                        {
                            if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                                string.Compare(xmlReader.Name, "musicFolder", StringComparison.Ordinal).Equals(0))
                            {
                                var folderId = xmlReader.GetAttribute("id");
                                var folderName = xmlReader.GetAttribute("name");
                                if (folderName != null && _folderLookup.ContainsKey(folderName))
                                {
                                    _folderLookup[folderName] = folderId;
                                }
                                else
                                {
                                    if (folderName != null) _folderLookup.Add(folderName, folderId);
                                }
                                collection.Add(new KeyValuePair<string, string>(folderId, folderName));
                            }
                        }
                    }
                    _collectionNames = new string[collection.Count - 1];
                    for (var index = 0; index < collection.Count - 1; index++)
                    {
                        _collectionNames[index] = collection[index].Value + @"\";
                    }
                    var isDirty = false;
                    foreach (var item in collection)
                    {
                        folders.AddRange(GetRootFolders(item.Key, item.Value, true, refresh && dirtyOnly, ref isDirty));
                    }
                    if (collectionOnly)
                    {
                        return collection;
                    }
                    if (dirtyOnly)
                    {
                        return null;
                    }
                }
            }
            return folders;
        }

        private static IEnumerable<KeyValuePair<string, string>> GetRootFolders(string collectionId,
            string collectionName,
            bool indices, bool updateIsDirty, ref bool isDirty)
        {
            var folders = new List<KeyValuePair<string, string>>();
            using (var stream = GetHttpRequestStream("getIndexes.view", "musicFolderId=" + collectionId))
            using (var xmlReader = new XmlTextReader(stream))
            {
                while (xmlReader.Read())
                {
                    if (xmlReader.NodeType.Equals(XmlNodeType.Element))
                    {
                        if (string.Compare(xmlReader.Name, "artist", StringComparison.Ordinal).Equals(0))
                        {
                            var folderId = xmlReader.GetAttribute("id");
                            var folderName = collectionName + @"\" + xmlReader.GetAttribute("name");
                            if (_folderLookup.ContainsKey(folderName))
                            {
                                _folderLookup[folderName] = folderId;
                            }
                            else
                            {
                                _folderLookup.Add(folderName, folderId);
                            }
                            folders.Add(new KeyValuePair<string, string>(indices ? folderId : folderName, collectionName));
                        }
                        else if (updateIsDirty &&
                                 string.Compare(xmlReader.Name, "indexes", StringComparison.Ordinal).Equals(0))
                        {
                            ulong serverLastModified;
                            if (ulong.TryParse(xmlReader.GetAttribute("lastModified"), out serverLastModified))
                            {
                                lock (_cacheFileLock)
                                {
                                    ulong clientLastModified;
                                    if (!_lastModified.TryGetValue(collectionName, out clientLastModified))
                                    {
                                        isDirty = true;
                                        _lastModified.Add(collectionName, serverLastModified);
                                    }
                                    else if (serverLastModified > clientLastModified)
                                    {
                                        isDirty = true;
                                        _lastModified[collectionName] = serverLastModified;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return folders;
        }

        private static void GetFolderFiles(string baseFolderName, string folderId,
            ICollection<KeyValuePair<byte, string>[]> files)
        {
            using (var stream = GetHttpRequestStream("getMusicDirectory.view", "id=" + folderId))
            using (var xmlReader = new XmlTextReader(stream))
            {
                while (xmlReader.Read())
                {
                    if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                        string.Compare(xmlReader.Name, "child", StringComparison.Ordinal).Equals(0))
                    {
                        if (string.Compare(xmlReader.GetAttribute("isDir"), "true", StringComparison.Ordinal).Equals(0))
                        {
                            GetFolderFiles(baseFolderName, xmlReader.GetAttribute("id"), files);
                        }
                        else
                        {
                            var tags = GetTags(xmlReader, baseFolderName);
                            if (tags != null)
                            {
                                files.Add(tags);
                            }
                        }
                    }
                }
                xmlReader.Close();
            }
        }

        private static KeyValuePair<byte, string>[][] GetFolderFiles(string path)
        {
            if (!path.EndsWith(@"\"))
            {
                path += @"\";
            }
            var folderId = GetFolderId(path);
            var files = new List<KeyValuePair<byte, string>[]>();
            if (folderId == null)
            {
                return new KeyValuePair<byte, string>[][] {};
            }
            GetFolderFiles(path.Substring(0, path.IndexOf(@"\", StringComparison.Ordinal)), folderId, files);
            return files.ToArray();
        }

        private static string GetFolderId(string url)
        {
            var charIndex = url.LastIndexOf(@"\", StringComparison.Ordinal);
            if (charIndex.Equals(-1))
            {
                throw new ArgumentException();
            }
            if (_folderLookup.Count.Equals(0))
            {
                GetRootFolders(false, false, false);
            }
            string folderId;
            if (!_folderLookup.TryGetValue(url.Substring(0, charIndex), out folderId))
            {
                var sectionStartIndex = url.IndexOf(@"\", StringComparison.Ordinal) + 1;
                charIndex = url.IndexOf(@"\", sectionStartIndex, StringComparison.Ordinal);
                if (charIndex.Equals(-1))
                {
                    throw new ArgumentException();
                }
                while (charIndex != -1)
                {
                    string subFolderId;
                    if (_folderLookup.TryGetValue(url.Substring(0, charIndex), out subFolderId))
                    {
                        folderId = subFolderId;
                    }
                    else
                    {
                        var folderName = url.Substring(sectionStartIndex, charIndex - sectionStartIndex);
                        using (var stream = GetHttpRequestStream("getMusicDirectory.view", "id=" + folderId))
                        using (var xmlReader = new XmlTextReader(stream))
                        {
                            while (xmlReader.Read())
                            {
                                if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                                    string.Compare(xmlReader.Name, "child", StringComparison.Ordinal).Equals(0))
                                {
                                    if (
                                        string.Compare(xmlReader.GetAttribute("isDir"), "true", StringComparison.Ordinal)
                                            .Equals(0) &&
                                        string.Compare(xmlReader.GetAttribute("title"), folderName,
                                            StringComparison.Ordinal).Equals(0))
                                    {
                                        folderId = xmlReader.GetAttribute("id");
                                        _folderLookup.Add(url.Substring(0, charIndex), folderId);
                                        break;
                                    }
                                }
                            }
                            xmlReader.Close();
                        }
                    }
                    sectionStartIndex = charIndex + 1;
                    charIndex = url.IndexOf(@"\", sectionStartIndex, StringComparison.Ordinal);
                }
            }
            return folderId;
        }

        private static string GetTranslatedUrl(string url)
        {
            return url.Replace(@"\", @"/");
        }

        private static string GetFileId(string url)
        {
            var folderId = GetFolderId(url);
            if (folderId != null)
            {
                using (var stream = GetHttpRequestStream("getMusicDirectory.view", "id=" + folderId))
                using (var xmlReader = new XmlTextReader(stream))
                {
                    var filePath = GetTranslatedUrl(url.Substring(url.IndexOf(@"\", StringComparison.Ordinal) + 1));
                    while (xmlReader.Read())
                    {
                        if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                            string.Compare(xmlReader.Name, "child", StringComparison.Ordinal).Equals(0))
                        {
                            if (
                                string.Compare(xmlReader.GetAttribute("path"), filePath, StringComparison.Ordinal)
                                    .Equals(0))
                            {
                                return xmlReader.GetAttribute("id");
                            }
                        }
                    }
                    xmlReader.Close();
                }
            }
            return null;
        }

        private static string GetResolvedUrl(string url)
        {
            if (_folderLookup.Count.Equals(0))
            {
                GetRootFolders(false, false, false);
            }
            if (_collectionNames.Length.Equals(1))
            {
                return _collectionNames[0] + url;
            }
            var path = url.Substring(0, url.LastIndexOf(@"\", StringComparison.Ordinal));
            string lastMatch = null;
            var count = 0;
            for (var index = 0; index < _collectionNames.Length - 1; index++)
            {
                if (GetFolderId(_collectionNames[index] + path) != null)
                {
                    count += 1;
                    lastMatch = _collectionNames[index] + url;
                }
            }
            if (count.Equals(1))
            {
                return lastMatch;
            }
            for (var index = 0; index < _collectionNames.Length - 1; index++)
            {
                if (GetFolderId(_collectionNames[index] + path) != null)
                {
                    lastMatch = _collectionNames[index] + url;
                    if (GetFileId(lastMatch) != null)
                    {
                        return lastMatch;
                    }
                }
            }
            return url;
        }

        private static string GetCoverArtId(string url)
        {
            var folderId = GetFolderId(url);
            if (folderId != null)
            {
                using (var stream = GetHttpRequestStream("getMusicDirectory.view", "id=" + folderId))
                using (var xmlReader = new XmlTextReader(stream))
                {
                    var filePath = GetTranslatedUrl(url.Substring(url.IndexOf(@"\", StringComparison.Ordinal) + 1));
                    while (xmlReader.Read())
                    {
                        if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                            string.Compare(xmlReader.Name, "child", StringComparison.Ordinal).Equals(0))
                        {
                            if (
                                string.Compare(xmlReader.GetAttribute("path"), filePath, StringComparison.Ordinal)
                                    .Equals(0))
                            {
                                return xmlReader.GetAttribute("coverArt");
                            }
                        }
                    }
                    xmlReader.Close();
                }
            }
            return null;
        }

        public static bool FileExists(string url)
        {
            return GetFileId(url) != null;
        }

        public static KeyValuePair<byte, string>[] GetFile(string url)
        {
            var folderId = GetFolderId(url);
            if (folderId != null)
            {
                using (var stream = GetHttpRequestStream("getMusicDirectory.view", "id=" + folderId))
                using (var xmlReader = new XmlTextReader(stream))
                {
                    var filePath = GetTranslatedUrl(url.Substring(url.IndexOf(@"\", StringComparison.Ordinal) + 1));
                    while (xmlReader.Read())
                    {
                        if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                            string.Compare(xmlReader.Name, "child", StringComparison.Ordinal).Equals(0))
                        {
                            if (
                                string.Compare(xmlReader.GetAttribute("path"), filePath, StringComparison.Ordinal)
                                    .Equals(0))
                            {
                                return GetTags(xmlReader, null);
                            }
                        }
                    }
                    xmlReader.Close();
                }
            }
            return null;
        }

        private static KeyValuePair<byte, string>[] GetTags(XmlTextReader xmlReader, string baseFolderName)
        {
            if (string.Compare(xmlReader.GetAttribute("isVideo"), "true", StringComparison.Ordinal).Equals(0))
            {
                return null;
            }
            var tags = new KeyValuePair<byte, string>[TagCount];
            var path = string.Empty;
            var attribute = xmlReader.GetAttribute("path");
            if (attribute != null)
            {
                path = attribute.Replace(@"/", @"\");
            }
            if (baseFolderName == null)
            {
                path = GetResolvedUrl(path);
            }
            else
            {
                path = baseFolderName + @"\" + path;
            }
            tags[0] = new KeyValuePair<byte, string>((byte) Plugin.FilePropertyType.Url, path);
            tags[1] = new KeyValuePair<byte, string>((byte) Plugin.MetaDataType.Artist, xmlReader.GetAttribute("artist"));
            tags[2] = new KeyValuePair<byte, string>((byte) Plugin.MetaDataType.TrackTitle,
                xmlReader.GetAttribute("title"));
            tags[3] = new KeyValuePair<byte, string>((byte) Plugin.MetaDataType.Album, xmlReader.GetAttribute("album"));
            tags[4] = new KeyValuePair<byte, string>((byte) Plugin.MetaDataType.Year, xmlReader.GetAttribute("year"));
            tags[5] = new KeyValuePair<byte, string>((byte) Plugin.MetaDataType.TrackNo, xmlReader.GetAttribute("track"));
            tags[6] = new KeyValuePair<byte, string>((byte) Plugin.MetaDataType.Genre, xmlReader.GetAttribute("genre"));
            int duration;
            if (int.TryParse(xmlReader.GetAttribute("duration"), out duration))
            {
                tags[7] = new KeyValuePair<byte, string>((byte) Plugin.FilePropertyType.Duration,
                    (duration*1000).ToString());
            }
            tags[8] = new KeyValuePair<byte, string>((byte) Plugin.FilePropertyType.Bitrate,
                xmlReader.GetAttribute("bitRate"));
            tags[9] = new KeyValuePair<byte, string>((byte) Plugin.FilePropertyType.Size, xmlReader.GetAttribute("size"));
            tags[10] = new KeyValuePair<byte, string>((byte) Plugin.MetaDataType.Artwork,
                string.IsNullOrEmpty(xmlReader.GetAttribute("coverArt")) ? "" : "Y");

            for (var tagIndex = 1; tagIndex < TagCount - 2; tagIndex++)
            {
                if (tags[tagIndex].Value == null)
                {
                    tags[tagIndex] = new KeyValuePair<byte, string>(tags[tagIndex].Key, "");
                }
            }
            return tags;
        }

        public static byte[] GetFileArtwork(string url)
        {
            _lastEx = null;
            byte[] bytes = null;
            try
            {
                var id = GetCoverArtId(url);
                if (id != null)
                {
                    using (var stream = GetHttpRequestStream("getCoverArt.view", "id=" + id))
                    {
                        if (string.Compare(stream.ContentType, "text/xml", StringComparison.Ordinal) != 0)
                        {
                            bytes = stream.ToArray();
                        }
                        else
                        {
                            _lastEx =
                                new InvalidDataException(GetErrorMessage(Encoding.UTF8.GetString(stream.ToArray())));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastEx = ex;
            }
            return bytes;
        }

        public static KeyValuePair<string, string>[] GetPlaylists()
        {
            _lastEx = null;
            var playlists = new List<KeyValuePair<string, string>>();
            using (var stream = GetHttpRequestStream("getPlaylists.view", null))
            using (var xmlReader = new XmlTextReader(stream))
            {
                while (xmlReader.Read())
                {
                    if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                        string.Compare(xmlReader.Name, "playlist", StringComparison.Ordinal).Equals(0))
                    {
                        playlists.Add(new KeyValuePair<string, string>(xmlReader.GetAttribute("id"),
                            xmlReader.GetAttribute("name")));
                    }
                }
            }
            return playlists.ToArray();
        }

        public static KeyValuePair<byte, string>[][] GetPlaylistFiles(string id)
        {
            _lastEx = null;
            using (var stream = GetHttpRequestStream("getPlaylist.view", "id=" + id))
            using (var xmlReader = new XmlTextReader(stream))
            {
                var files = new List<KeyValuePair<byte, string>[]>();
                while (xmlReader.Read())
                {
                    if (xmlReader.NodeType.Equals(XmlNodeType.Element) &&
                        string.Compare(xmlReader.Name, "entry", StringComparison.Ordinal).Equals(0))
                    {
                        var tags = GetTags(xmlReader, null);
                        if (tags != null)
                        {
                            files.Add(tags);
                        }
                    }
                }
                xmlReader.Close();
                return files.ToArray();
            }
        }

        public static Stream GetStream(string url)
        {
            _lastEx = null;
            var id = GetFileId(url);
            if (id == null)
            {
                _lastEx = new FileNotFoundException();
            }
            else
            {
                var stream = GetHttpRequestStream(Transcode ? "stream.view" : "download.view", "id=" + id);
                if (string.Compare(stream.ContentType, "text/xml", StringComparison.Ordinal) != 0)
                {
                    return stream;
                }
                using (stream)
                {
                    _lastEx = new InvalidDataException(GetErrorMessage(Encoding.UTF8.GetString(stream.ToArray())));
                }
            }
            return null;
        }

        public static Exception GetError()
        {
            return _lastEx;
        }

        private static string GetHttpRequestXml(string query, string parameters, int timeout)
        {
            using (var stream = GetHttpRequestStream(query, parameters, timeout))
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static ConnectStream GetHttpRequestStream(string query, string parameters, int timeout = 30000)
        {
            return
                new ConnectStream(
                    _serverName + "rest/" + query + "?u=" + Username + "&p=" + Password + "&v=1.0.0&c=MusicBee" +
                    (string.IsNullOrEmpty(parameters) ? "" : "&" + parameters), timeout);
        }

        private sealed class FileSorter : Comparer<KeyValuePair<byte, string>[]>
        {
            public override int Compare(KeyValuePair<byte, string>[] x, KeyValuePair<byte, string>[] y)
            {
                if (x != null && y != null)
                {
                    return string.Compare(x[0].Value, y[0].Value, StringComparison.OrdinalIgnoreCase);
                }
                return 0;
            }
        }

        private sealed class ConnectStream : Stream
        {
            public readonly string ContentType;
            private Stream _responseStream;
            private HttpWebResponse _webResponse;

            public ConnectStream(string url, int timeout)
            {
                var httpRequest = (HttpWebRequest) WebRequest.Create(url);
                httpRequest.Accept = "*/*";
                httpRequest.Method = "GET";
                httpRequest.Timeout = timeout;
                _webResponse = (HttpWebResponse) httpRequest.GetResponse();
                ContentType = _webResponse.ContentType;
                Length = _webResponse.ContentLength;
                _responseStream = _webResponse.GetResponseStream();
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length { get; }

            public override long Position
            {
                get { return _responseStream.Position; }
                set { _responseStream.Position = value; }
            }

            protected override void Dispose(bool disposing)
            {
                Close();
            }

            /// <summary>
            /// Closes the current stream and releases any resources (such as sockets and file handles) associated with the current stream. Instead of calling this method, ensure that the stream is properly disposed.
            /// </summary>
            /// <filterpriority>1</filterpriority>
            public override void Close()
            {
                if (_responseStream != null)
                {
                    _responseStream.Close();
                    _responseStream = null;
                }
                if (_webResponse != null)
                {
                    _webResponse.Close();
                    _webResponse = null;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _responseStream.Read(buffer, offset, count);
            }

            public byte[] ToArray()
            {
                var length = (int) _webResponse.ContentLength;
                if (length <= 0)
                {
                    length = 4096;
                }
                using (var memoryStream = new MemoryStream(length))
                {
                    var buffer = new byte[4095];
                    do
                    {
                        var bytes = _responseStream.Read(buffer, 0, 4096);
                        if (bytes.Equals(0))
                        {
                            break;
                        }
                        memoryStream.Write(buffer, 0, bytes);
                    } while (true);
                    return memoryStream.ToArray();
                }
            }

            public override long Seek(long offset, SeekOrigin origin) => 0;

            public override void SetLength(long value)
            {
            }

            public override void Flush()
            {
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
            }
        }
    }
}