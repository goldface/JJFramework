﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JJFramework.Runtime.Extension;
using UniRx;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace JJFramework.Runtime
{
    public class AssetBundleManager
    {
        public enum STATE
        {
            INITIALIZING,
            PREPARING,
            DOWNLOADING,
            LOADING,
            LOADED,
            
            ERROR
        }
        
        private AssetBundle _assetBundleManifestObject;
        private AssetBundleManifest _assetBundleManifest;
        private readonly Dictionary<string, AssetBundle> _assetBundleList = new Dictionary<string, AssetBundle>(0);
        private readonly List<string> _downloadingAssetBundleList = new List<string>(0);

        private static readonly string MANIFEST_NAME = "AssetBundleManifest";
        private static readonly string CONTENT_LENGTH = "Content-Length";
        
        public string downloadUrl { get; private set; }
        public string localAssetBundlePath { get; private set; }

        public STATE state { get; private set; }

        public int maximumAssetBundleCount { get; private set; }
        public int downloadedAssetBundleCount { get; private set; }
        public int loadedAssetBundleCount { get; private set; }
        public float currentAssetBundleProgress { get; private set; }
        public ulong assetBundleTotalSize { get; private set; }

        private int _skippedAssetBundleCount;

        public void UnloadAssetBundle(string assetBundleName, bool unloadAll = true)
        {
            AssetBundle assetBundle;
            if (!_assetBundleList.TryGetValue(assetBundleName, out assetBundle))
            {
                Debug.LogError($"[UnloadAssetBundle] Failed to unload - {assetBundleName} is NOT FOUND!");
                return;
            }

            assetBundle.Unload(unloadAll);
            _assetBundleList.Remove(assetBundleName);
        }

        public void UnloadAllAssetBundle(bool unloadManifest, bool unloadAll = true)
        {
            foreach (var assetBundle in _assetBundleList)
            {
                assetBundle.Value.Unload(unloadAll);
            }

            _assetBundleList.Clear();
            
            if (unloadManifest)
            {
                if (_assetBundleManifestObject)
                {
                    _assetBundleManifestObject.Unload(true);
                    _assetBundleManifestObject = null;
                }

                if (_assetBundleManifest)
                {
                    _assetBundleManifest = null;
                }
            }
        }

        public void Cleanup()
        {
            UnloadAllAssetBundle(true);
            
            _assetBundleList.Clear();
            _assetBundleManifest = null;
            _assetBundleManifestObject = null;
        }

        private IEnumerator DownloadAssetBundle(string assetBundleName)
        {
            // NOTE(JJO): 먼저 Bundle의 Manifest 요청/검증
            var manifestPath = downloadUrl.EndsWith("/") == false
                ? $"{downloadUrl}/{assetBundleName}.manifest"
                : $"{downloadUrl}{assetBundleName}.manifest";
            using (var request = UnityWebRequest.Get(manifestPath))
            {
                yield return request.SendWebRequest();
                
                if (request.isNetworkError ||
                    request.isHttpError ||
                    string.IsNullOrEmpty(request.error) == false)
                {
                    Debug.LogError($"[DownloadAssetBundle] Network Error!\n{request.error}");
                    state = STATE.ERROR;
                    yield break;
                }

                var path = Path.Combine(localAssetBundlePath, $"{assetBundleName}.manifest");
                if (File.Exists(path))
                {
                    var localManifest = File.ReadAllText(path);
                    if (request.downloadHandler.text == localManifest)
                    {
                        Debug.Log($"[DownloadAssetBundle] Skipped to download - {assetBundleName} is latest version!");
                        ++downloadedAssetBundleCount;
                        ++_skippedAssetBundleCount;
                        yield break;
                    }
                }
                
                File.WriteAllText(path, request.downloadHandler.text);
            }
            
            var savePath = Path.Combine(localAssetBundlePath, assetBundleName);
            var downloadPath = downloadUrl.EndsWith("/") == false ? $"{downloadUrl}/{assetBundleName}" : $"{downloadUrl}{assetBundleName}";
            using (var request = new UnityWebRequest(downloadPath, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(savePath), null))
            {
                request.SendWebRequest();
                while (request.isDone == false)
                {
                    currentAssetBundleProgress = request.downloadProgress;
                    yield return null;
                }

                if (request.isNetworkError ||
                    request.isHttpError ||
                    string.IsNullOrEmpty(request.error) == false)
                {
                    Debug.LogError($"[DownloadAssetBundle] Network Error!\n{request.error}");
                    state = STATE.ERROR;
                    yield break;
                }

                ++downloadedAssetBundleCount;
                Debug.Log($"[DownloadAssetBundle] Succeeded to download - {savePath}");
            }
        }

        public IEnumerator PrepareDownload(string url, string manifestName, string assetBundleDirectoryName)
        {
            state = STATE.PREPARING;

            downloadUrl = url;
            localAssetBundlePath = Path.Combine(Application.persistentDataPath, assetBundleDirectoryName);
            if (Directory.Exists(localAssetBundlePath) == false)
            {
                Directory.CreateDirectory(localAssetBundlePath);
            }
            
            var localManifestRawPath = Path.Combine(localAssetBundlePath, $"{manifestName}.manifest");
            var localManifestPath = Path.Combine(localAssetBundlePath, manifestName);
            string localManifestRaw = string.Empty;
            string remoteManifestRaw = string.Empty;
            
            // NOTE(JJO): 로컬에 저장된 데이터가 없다면 StreamingAssets를 검사하여 모든 StreamingAssets의 AssetBundle을 가져옴
            if (File.Exists(localManifestRawPath) == false)
            {
                var streamingAssetsPath = Path.Combine(Application.streamingAssetsPath, "Bundle");
                if (Directory.Exists(streamingAssetsPath))
                {
                    var fileList = Directory.GetFiles(streamingAssetsPath);
                    var fileListCount = fileList.Length;
                    for (int i = 0; i < fileListCount; ++i)
                    {
                        if (fileList[i].Contains(".meta"))
                        {
                            continue;
                        }
                        
                        var fileName = Path.GetFileName(fileList[i]);
                        var destinationPath = Path.Combine(localAssetBundlePath, fileName);
                        File.Copy(fileList[i], destinationPath);
                        Debug.Log($"[PrepareDownload] Copy {fileList[i]} to {destinationPath}");
                    }
                }
            }

            // NOTE(JJO): 다시 로컬에 있는지 검사하여 가져옴
            if (File.Exists(localManifestRawPath))
            {
                localManifestRaw = File.ReadAllText(localManifestRawPath);
            }

            var remoteManifestRawPath = downloadUrl.EndsWith("/") == false ? $"{downloadUrl}/{manifestName}.manifest" : $"{downloadUrl}{manifestName}.manifest";
            using (var request = UnityWebRequest.Get(remoteManifestRawPath))
            {
                yield return request.SendWebRequest();

                if (string.IsNullOrEmpty(request.error) == false)
                {
                    Debug.LogError($"[PrepareDownload] Failed to load a manifest from {remoteManifestRawPath}\n{request.error}");
                    state = STATE.ERROR;
                    yield break;
                }

                remoteManifestRaw = request.downloadHandler.text;
            }
            
            if (string.IsNullOrEmpty(localManifestRaw) ||
                localManifestRaw != remoteManifestRaw)
            {
                File.WriteAllText(localManifestRawPath, remoteManifestRaw);
                
                // NOTE(JJO): 먼저 AssetBundleManifest를 받아서 정보를 가져옴.
                var remoteManifestPath = downloadUrl.EndsWith("/") == false ? $"{downloadUrl}/{manifestName}" : $"{downloadUrl}{manifestName}";

                using (var request = UnityWebRequest.Get(remoteManifestPath))
                {
                    yield return request.SendWebRequest();

                    if (request.isNetworkError ||
                        request.isHttpError ||
                        string.IsNullOrEmpty(request.error) == false)
                    {
                        Debug.LogError($"[PrepareDownload] Failed to load a manifest from {remoteManifestPath}\n{request.error}");
                        state = STATE.ERROR;
                        yield break;
                    }

                    _assetBundleManifestObject = AssetBundle.LoadFromMemory(request.downloadHandler.data, 0);
                    
                    File.WriteAllBytes(localManifestPath, request.downloadHandler.data);
                }
            }
            else
            {
                _assetBundleManifestObject = AssetBundle.LoadFromFile(localManifestPath);
            }
            
            _assetBundleManifest = _assetBundleManifestObject.LoadAsset<AssetBundleManifest>(MANIFEST_NAME);

            maximumAssetBundleCount = _assetBundleManifest.GetAllAssetBundles().Length;

            var localAssetBundleFileNameList = Directory.GetFiles(localAssetBundlePath).ToList();
            var assetList = _assetBundleManifest.GetAllAssetBundles();
            var listCount = assetList.Length;
            
            // NOTE(JJO): 쓰지 않는 AssetBundle 제거
            if (localAssetBundleFileNameList.Count > 0)
            {
                for (var assetIndex = 0; assetIndex < listCount; ++assetIndex)
                {
                    var localAssetBundleFileNameListCount = localAssetBundleFileNameList.Count;
                    for (var fileIndex = localAssetBundleFileNameListCount - 1; fileIndex >= 0; --fileIndex)
                    {
                        var fileName = Path.GetFileName(localAssetBundleFileNameList[fileIndex]);
                        if (fileName == manifestName ||
                            fileName == $"{manifestName}.manifest" ||
                            fileName == assetList[assetIndex] ||
                            fileName == $"{assetList[assetIndex]}.manifest")
                        {
                            localAssetBundleFileNameList.RemoveAt(fileIndex);
                        }
                    }
                }
            }

            if (localAssetBundleFileNameList.Count > 0)
            {
                var localAssetBundleFileNameListCount = localAssetBundleFileNameList.Count;
                for (var fileIndex = localAssetBundleFileNameListCount - 1; fileIndex >= 0; --fileIndex)
                {
                    File.Delete(localAssetBundleFileNameList[fileIndex]);
                }
            }

            // NOTE(JJO): 받아야하는 에셋의 크기를 계산한다!
            assetBundleTotalSize = 0;
            for (int i = 0; i < listCount; ++i)
            {
                var downloadPath = downloadUrl.EndsWith("/") == false ? $"{downloadUrl}/{assetList[i]}" : $"{downloadUrl}{assetList[i]}";
                remoteManifestRawPath = $"{downloadPath}.manifest";
                remoteManifestRaw = string.Empty;
                localManifestRawPath = Path.Combine(localAssetBundlePath, $"{assetList[i]}.manifest");
                if (File.Exists(localManifestRawPath))
                {
                    localManifestRaw = File.ReadAllText(localManifestRawPath);
                }
                else
                {
                    localManifestRaw = string.Empty;
                }

                using (var request = UnityWebRequest.Get(remoteManifestRawPath))
                {
                    yield return request.SendWebRequest();

                    if (string.IsNullOrEmpty(request.error) == false)
                    {
                        Debug.LogError($"[PrepareDownload] Failed to download a {assetList[i]}.manifest from {remoteManifestRawPath}!\n{request.error}");
                        state = STATE.ERROR;
                        yield break;
                    }

                    remoteManifestRaw = request.downloadHandler.text;
                }

                if (string.IsNullOrEmpty(localManifestRaw) ||
                    localManifestRaw != remoteManifestRaw)
                {
                    // NOTE(JJO): 받아야 하는 에셋의 크기를 구한다.
                    using (var request = UnityWebRequest.Head(downloadPath))
                    {
                        yield return request.SendWebRequest();

                        if (request.isNetworkError ||
                            request.isHttpError)
                        {
                            Debug.LogError($"[PrepareDownload] Failed to get size of a {assetList[i]} from {downloadPath}!\n{request.error}");
                            state = STATE.ERROR;
                            yield break;
                        }

                        ulong assetBundleSize;
                        ulong.TryParse(request.GetResponseHeader(CONTENT_LENGTH), out assetBundleSize);
                        assetBundleTotalSize += assetBundleSize;
                        
                        _downloadingAssetBundleList.Add(assetList[i]);
                    }
                }
            }
        }

        public IEnumerator DownloadAllAssetBundle()
        {
            _skippedAssetBundleCount = 0;
            downloadedAssetBundleCount = 0;
            currentAssetBundleProgress = 0f;

            var assetList = _downloadingAssetBundleList;// _assetBundleManifest.GetAllAssetBundles();
            var listCount = assetList.Count;
        
            state = STATE.DOWNLOADING;

            // NOTE(JJO): 로컬에 AssetBundle Download
            for (var i = 0; i < listCount; ++i)
            {
                yield return DownloadAssetBundle(assetList[i]);
                if (state == STATE.ERROR)
                {
                    yield break;
                }
            }

            // NOTE(JJO): 모두 Skip이 아니라면 캐시를 비우도록 함! (Test)
            if (_skippedAssetBundleCount != listCount)
            {
                Caching.ClearCache();
            }
        }

        public IEnumerator PreloadAllAssetBundle()
        {
            if (_assetBundleManifest == null)
            {
                Debug.LogError($"[PreloadAllAssetBundle] AssetBundleManifest is NULL!");
                yield break;
            }

            state = STATE.LOADING;
            
            loadedAssetBundleCount = 0;

            var assetList = _assetBundleManifest.GetAllAssetBundles();
            var listCount = assetList.Length;
            for (var i = 0; i < listCount; ++i)
            {
                yield return LoadAssetBundle(assetList[i]);
                if (state == STATE.ERROR)
                {
                    yield break;
                }
                
                // TODO(JJO): Dependency Load
                var dependencies = _assetBundleManifest.GetAllDependencies(assetList[i]);
                var dependencyCount = dependencies.Length;
                maximumAssetBundleCount += dependencyCount;
                for (var depIndex = 0; depIndex < dependencyCount; ++depIndex)
                {
                    yield return LoadAssetBundle(dependencies[depIndex]);
                    if (state == STATE.ERROR)
                    {
                        yield break;
                    }
                }
            }

            state = STATE.LOADED;
        }

        private IEnumerator LoadAssetBundle(string assetBundle)
        {
            if (_assetBundleList.ContainsKey(assetBundle))
            {
                Debug.LogWarning($"[LoadAssetBundle] Already loaded asset - {assetBundle}");
                ++loadedAssetBundleCount;
                yield break;
            }
            
            var path = Path.Combine(localAssetBundlePath, assetBundle);
            using (var request = UnityWebRequestAssetBundle.GetAssetBundle(path, 0))
            {
                request.SendWebRequest();
                while (request.isDone == false)
                {
                    currentAssetBundleProgress = request.downloadProgress;
                    yield return null;
                }

                var bundle = DownloadHandlerAssetBundle.GetContent(request);
                if (bundle == null)
                {
                    Debug.LogError($"[LoadAssetBundle] Failed to load - {assetBundle} is NULL\n{request.error}");
                    state = STATE.ERROR;
                    yield break;
                }
                
                _assetBundleList.Add(assetBundle, bundle);
                    
                ++loadedAssetBundleCount;
                Debug.Log($"[LoadAssetBundle] Succeeded to load - {assetBundle}");
            }
        }

        public AssetBundle GetAssetBundle(string assetBundleName)
        {
            AssetBundle bundle;
            return _assetBundleList.TryGetValue(assetBundleName, out bundle) == false ? null : bundle;
        }

        public T LoadAsset<T>(string assetBundleName) where T : Object
        {
            return LoadAsset<T>(assetBundleName, assetBundleName);
        }

        public T LoadAsset<T>(string assetBundleName, string assetName) where T : Object
        {
            AssetBundle assetBundle;
            if (_assetBundleList.TryGetValue(assetBundleName, out assetBundle) == false)
            {
                Debug.LogError($"[LoadAsset] Failed to load - {assetBundleName} is NOT EXIST!");
                return null;
            }
            
            var result = assetBundle.LoadAsset<T>(assetName);
            if (result != null)
            {
                return result;
            }

            Debug.LogError($"[LoadAsset] Failed to load - {assetName} is NULL!");
            return null;
        }

        public IEnumerator LoadAssetAsync<T>(string assetBundleName, System.Action<T> outAction) where T : Object
        {
            yield return LoadAssetAsync<T>(assetBundleName, assetBundleName, outAction);
        }

        public IEnumerator LoadAssetAsync<T>(string assetBundleName, string assetName, System.Action<T> outAction)
            where T : Object
        {
//#if UNITY_EDITOR
//            if (SimulateAssetBundleInEditor)
//            {
//                var result = AssetDatabase.LoadAssetAtPath<T>(assetName);
//                if (result == null)
//                {
//                    Log.Error($"[LoadAssetBundle] Failed to load - {assetName} is NULL!");
//                }
//                
//                outAction(result);
//                
//                yield break;
//            }
//#endif
            AssetBundle assetBundle;
            if (_assetBundleList.TryGetValue(assetBundleName, out assetBundle) == false)
            {
                Debug.LogError($"[LoadAssetAsync] Failed to load - {assetBundleName} is NOT EXIST!");
                yield break;
            }

            var request = assetBundle.LoadAssetAsync<T>(assetName);
            yield return request;

            if (request.asset != null)
            {
                outAction(request.asset as T);
            }
            else
            {
                Debug.LogError($"[LoadAssetAsync] Failed to load - {assetName} is NULL!");
            }
        }

        public IEnumerator LoadLevelAsync(string assetBundleName, string levelName, bool isAdditive)
        {
            if (_assetBundleList.ContainsKey(assetBundleName) == false)
            {
                Debug.LogError($"[LoadLevelAsync] {assetBundleName} is NULL!");
                yield break;
            }
            
            yield return SceneManager.LoadSceneAsync(levelName, isAdditive ? LoadSceneMode.Additive : LoadSceneMode.Single);
        }

        public async Task<T> LoadAssetAsync<T>(string assetBundleName) where T : Object
        {
            var result = await LoadAssetAsync<T>(assetBundleName, assetBundleName);
            return result;
        }

        public async Task<T> LoadAssetAsync<T>(string assetBundleName, string assetName) where T : Object
        {
            AssetBundle assetBundle;
            if (_assetBundleList.TryGetValue(assetBundleName, out assetBundle) == false)
            {
                Debug.LogError($"[LoadAssetBundleAsync] Failed to load - {assetBundleName} is NOT EXIST!");
                return null;
            }

            var request = assetBundle.LoadAssetAsync<T>(assetName);
            await request;

            return request.asset as T;
        }
    }
}
