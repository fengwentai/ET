﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;

#endif

namespace ET
{
    [ObjectSystem]
    public class ABInfoAwakeSystem: AwakeSystem<ABInfo, string, AssetBundle>
    {
        public override void Awake(ABInfo self, string abName, AssetBundle a)
        {
            self.AssetBundle = a;
            self.Name = abName;
            self.RefCount = 1;
        }
    }

    [ObjectSystem]
    public class ABInfoDestroySystem: DestroySystem<ABInfo>
    {
        public override void Destroy(ABInfo self)
        {
            //Log.Debug($"desdroy assetbundle: {this.Name}");

            self.RefCount = 0;
            self.Name = "";
        }
    }

    public class ABInfo: Entity
    {
        public string Name
        {
            get;
            set;
        }

        public int RefCount
        {
            get;
            set;
        }

        public AssetBundle AssetBundle;

        public void Destroy(bool unload = true)
        {
            if (this.AssetBundle != null)
            {
                this.AssetBundle.Unload(unload);
            }

            this.Dispose();
        }
    }

    // 用于字符串转换，减少GC
    public static class AssetBundleHelper
    {
        public static string IntToString(this int value)
        {
            string result;
            if (ResourcesComponent.Instance.IntToStringDict.TryGetValue(value, out result))
            {
                return result;
            }

            result = value.ToString();
            ResourcesComponent.Instance.IntToStringDict[value] = result;
            return result;
        }

        public static string StringToAB(this string value)
        {
            string result;
            if (ResourcesComponent.Instance.StringToABDict.TryGetValue(value, out result))
            {
                return result;
            }

            result = value + ".unity3d";
            ResourcesComponent.Instance.StringToABDict[value] = result;
            return result;
        }

        public static string IntToAB(this int value)
        {
            return value.IntToString().StringToAB();
        }

        public static string BundleNameToLower(this string value)
        {
            string result;
            if (ResourcesComponent.Instance.BundleNameToLowerDict.TryGetValue(value, out result))
            {
                return result;
            }

            result = value.ToLower();
            ResourcesComponent.Instance.BundleNameToLowerDict[value] = result;
            return result;
        }
    }

    [ObjectSystem]
    public class ResourcesComponentAwakeSystem: AwakeSystem<ResourcesComponent>
    {
        public override void Awake(ResourcesComponent self)
        {
            self.Awake();
        }
    }

    public class ResourcesComponent: Entity
    {
        public static ResourcesComponent Instance
        {
            get;
            set;
        }

        public AssetBundleManifest AssetBundleManifestObject
        {
            get;
            set;
        }
        
        public Dictionary<int, string> IntToStringDict = new Dictionary<int, string>();

        public Dictionary<string, string> StringToABDict = new Dictionary<string, string>();

        public Dictionary<string, string> BundleNameToLowerDict = new Dictionary<string, string>() { { "StreamingAssets", "StreamingAssets" } };


        private readonly Dictionary<string, Dictionary<string, UnityEngine.Object>> resourceCache = new Dictionary<string, Dictionary<string, UnityEngine.Object>>();

        private readonly Dictionary<string, ABInfo> bundles = new Dictionary<string, ABInfo>();

        public void Awake()
        {
            Instance = this;
            
            if (Define.IsAsync)
            {
			    LoadOneBundle("StreamingAssets");
			    AssetBundleManifestObject = (AssetBundleManifest)GetAsset("StreamingAssets", "AssetBundleManifest");
            }
        }

        public override void Dispose()
        {
            if (this.IsDisposed)
            {
                return;
            }

            base.Dispose();

            Instance = null;

            foreach (KeyValuePair<string, ABInfo> abInfo in this.bundles)
            {
                abInfo.Value.Destroy();
            }

            this.bundles.Clear();
            this.resourceCache.Clear();
            this.IntToStringDict.Clear();
            this.StringToABDict.Clear();
            this.BundleNameToLowerDict.Clear();
        }

        private string[] GetDependencies(string assetBundleName, bool isScene = false)
        {
            string[] dependencies = new string[0];
            if (DependenciesCache.TryGetValue(assetBundleName, out dependencies))
            {
                return dependencies;
            }

            if (!Define.IsAsync)
            {
#if UNITY_EDITOR
                if (isScene == false)
                {
                    dependencies = AssetDatabase.GetAssetBundleDependencies(assetBundleName, true);
                }
#endif
            }
            else
            {
                dependencies = this.AssetBundleManifestObject.GetAllDependencies(assetBundleName);
            }

            DependenciesCache.Add(assetBundleName, dependencies);
            return dependencies;
        }

        public string[] GetSortedDependencies(string assetBundleName, bool isScene = false)
        {
            Dictionary<string, int> info = new Dictionary<string, int>();
            List<string> parents = new List<string>();
            CollectDependencies(parents, assetBundleName, info, isScene);
            string[] ss = info.OrderBy(x => x.Value).Select(x => x.Key).ToArray();
            return ss;
        }

        private void CollectDependencies(List<string> parents, string assetBundleName, Dictionary<string, int> info, bool isScene = false)
        {
            parents.Add(assetBundleName);
            string[] deps = GetDependencies(assetBundleName, isScene);
            foreach (string parent in parents)
            {
                if (!info.ContainsKey(parent))
                {
                    info[parent] = 0;
                }

                info[parent] += deps.Length;
            }

            foreach (string dep in deps)
            {
                if (parents.Contains(dep))
                {
                    throw new Exception($"包有循环依赖，请重新标记: {assetBundleName} {dep}");
                }

                CollectDependencies(parents, dep, info, isScene);
            }

            parents.RemoveAt(parents.Count - 1);
        }

        // 缓存包依赖，不用每次计算
        public static Dictionary<string, string[]> DependenciesCache = new Dictionary<string, string[]>();

        public bool Contains(string bundleName)
        {
            return this.bundles.ContainsKey(bundleName);
        }

        public Dictionary<string, UnityEngine.Object> GetBundleAll(string bundleName)
        {
            Dictionary<string, UnityEngine.Object> dict;
            if (!this.resourceCache.TryGetValue(bundleName.BundleNameToLower(), out dict))
            {
                throw new Exception($"not found asset: {bundleName}");
            }

            return dict;
        }

        public UnityEngine.Object GetAsset(string bundleName, string prefab)
        {
            Dictionary<string, UnityEngine.Object> dict;
            if (!this.resourceCache.TryGetValue(bundleName.BundleNameToLower(), out dict))
            {
                throw new Exception($"not found asset: {bundleName} {prefab}");
            }

            UnityEngine.Object resource = null;
            if (!dict.TryGetValue(prefab, out resource))
            {
                throw new Exception($"not found asset: {bundleName} {prefab}");
            }

            return resource;
        }

        public async ETTask UnloadBundles(List<string> bundleList, bool unload = true)
        {
            int i = 0;
            foreach (string bundle in bundleList)
            {
                using (await CoroutineLockComponent.Instance.Wait(CoroutineLockType.Resources, bundle.GetHashCode()))
                {
                    if (++i % 5 == 0)
                    {
                        await TimerComponent.Instance.WaitFrameAsync();
                    }
                    this.UnloadBundle(bundle, unload);
                }
            }
        }

        // 只允许场景设置unload为false
        public void UnloadBundle(string assetBundleName, bool unload = true)
        {
            assetBundleName = assetBundleName.BundleNameToLower();

            string[] dependencies = GetSortedDependencies(assetBundleName);

            //Log.Debug($"-----------dep unload start {assetBundleName} dep: {dependencies.ToList().ListToString()}");
            foreach (string dependency in dependencies)
            {
                this.UnloadOneBundle(dependency, unload);
            }

            //Log.Debug($"-----------dep unload finish {assetBundleName} dep: {dependencies.ToList().ListToString()}");
        }

        private void UnloadOneBundle(string assetBundleName, bool unload = true)
        {
            assetBundleName = assetBundleName.BundleNameToLower();

            ABInfo abInfo;
            if (!this.bundles.TryGetValue(assetBundleName, out abInfo))
            {
                return;
            }

            //Log.Debug($"---------------unload one bundle {assetBundleName} refcount: {abInfo.RefCount - 1}");

            --abInfo.RefCount;

            if (abInfo.RefCount > 0)
            {
                return;
            }
            
            //Log.Debug($"---------------truly unload one bundle {assetBundleName} refcount: {abInfo.RefCount}");
            this.bundles.Remove(assetBundleName);
            this.resourceCache.Remove(assetBundleName);
            abInfo.Destroy(unload);
            // Log.Debug($"cache count: {this.cacheDictionary.Count}");
        }

        /// <summary>
        /// 同步加载assetbundle
        /// </summary>
        /// <param name="assetBundleName"></param>
        /// <returns></returns>
        public void LoadBundle(string assetBundleName)
        {
            assetBundleName = assetBundleName.ToLower();

            string[] dependencies = GetSortedDependencies(assetBundleName);
            //Log.Debug($"-----------dep load start {assetBundleName} dep: {dependencies.ToList().ListToString()}");
            foreach (string dependency in dependencies)
            {
                if (string.IsNullOrEmpty(dependency))
                {
                    continue;
                }

                this.LoadOneBundle(dependency);
            }

            //Log.Debug($"-----------dep load finish {assetBundleName} dep: {dependencies.ToList().ListToString()}");
        }

        public void AddResource(string bundleName, string assetName, UnityEngine.Object resource)
        {
            Dictionary<string, UnityEngine.Object> dict;
            if (!this.resourceCache.TryGetValue(bundleName.BundleNameToLower(), out dict))
            {
                dict = new Dictionary<string, UnityEngine.Object>();
                this.resourceCache[bundleName] = dict;
            }

            dict[assetName] = resource;
        }

        private void LoadOneBundle(string assetBundleName)
        {
            assetBundleName = assetBundleName.BundleNameToLower();
            ABInfo abInfo;
            if (this.bundles.TryGetValue(assetBundleName, out abInfo))
            {
                ++abInfo.RefCount;
                //Log.Debug($"---------------load one bundle {assetBundleName} refcount: {abInfo.RefCount}");
                return;
            }
            
            if (!Define.IsAsync)
            {
                string[] realPath = null;
#if UNITY_EDITOR
                realPath = AssetDatabase.GetAssetPathsFromAssetBundle(assetBundleName);
                foreach (string s in realPath)
                {
                    string assetName = Path.GetFileNameWithoutExtension(s);
                    UnityEngine.Object resource = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(s);
                    AddResource(assetBundleName, assetName, resource);
                }

            
              
                if (realPath.Length > 0)
                {
                    abInfo = EntityFactory.CreateWithParent<ABInfo, string, AssetBundle>(this, assetBundleName, null);
                    this.bundles[assetBundleName] = abInfo;
                    //Log.Debug($"---------------load one bundle {assetBundleName} refcount: {abInfo.RefCount}");
                }
                else
                {
                    Log.Error($"assets bundle not found: {assetBundleName}");
                }
#endif
                return;
            }

            string p = Path.Combine(PathHelper.AppHotfixResPath, assetBundleName);
            AssetBundle assetBundle = null;
            if (File.Exists(p))
            {
                assetBundle = AssetBundle.LoadFromFile(p);
            }
            else
            {
                p = Path.Combine(PathHelper.AppResPath, assetBundleName);
                assetBundle = AssetBundle.LoadFromFile(p);
            }

            if (assetBundle == null)
            {
                // 获取资源的时候会抛异常，这个地方不直接抛异常，因为有些地方需要Load之后判断是否Load成功
                Log.Warning($"assets bundle not found: {assetBundleName}");
                return;
            }

            if (!assetBundle.isStreamedSceneAssetBundle)
            {
                // 异步load资源到内存cache住
                UnityEngine.Object[] assets = assetBundle.LoadAllAssets();
                foreach (UnityEngine.Object asset in assets)
                {
                    AddResource(assetBundleName, asset.name, asset);
                }
            }

            abInfo = EntityFactory.CreateWithParent<ABInfo, string, AssetBundle>(this, assetBundleName, assetBundle);
            this.bundles[assetBundleName] = abInfo;
            
            //Log.Debug($"---------------load one bundle {assetBundleName} refcount: {abInfo.RefCount}");
        }

        /// <summary>
        /// 异步加载assetbundle
        /// </summary>
        /// <param name="assetBundleName"></param>
        /// <param name="isScene"></param>
        /// <returns></returns>
        public async ETTask LoadBundleAsync(string assetBundleName, bool isScene = false)
        {
            assetBundleName = assetBundleName.BundleNameToLower();
            
            string[] dependencies = GetSortedDependencies(assetBundleName);
            //Log.Debug($"-----------dep load async start {assetBundleName} dep: {dependencies.ToList().ListToString()}");
            foreach (string dependency in dependencies)
            {
                if (string.IsNullOrEmpty(dependency))
                {
                    continue;
                }

                using (await CoroutineLockComponent.Instance.Wait(CoroutineLockType.Resources, dependency.GetHashCode()))
                {
                    await this.LoadOneBundleAsync(dependency, isScene);
                }
            }
        }
        
        private async ETTask LoadOneBundleAsync(string assetBundleName, bool isScene)
        {
            assetBundleName = assetBundleName.BundleNameToLower();
            ABInfo abInfo;
            if (this.bundles.TryGetValue(assetBundleName, out abInfo))
            {
                ++abInfo.RefCount;
                //Log.Debug($"---------------load one bundle {assetBundleName} refcount: {abInfo.RefCount}");
                return;
            }

            string p = "";
            AssetBundle assetBundle = null;
            
            if (!Define.IsAsync)
            {
#if UNITY_EDITOR

                if (isScene)
                {
                    p = Path.Combine(Application.dataPath, "../../AssetBundles/Windows_Scene/", assetBundleName);
                    if (File.Exists(p)) // 如果场景有预先打包
                    {
                        using (AssetsBundleLoaderAsync assetsBundleLoaderAsync = EntityFactory.CreateWithParent<AssetsBundleLoaderAsync>(this))
                        {
                            assetBundle = await assetsBundleLoaderAsync.LoadAsync(p);
                        }

                        if (assetBundle == null)
                        {
                            // 获取资源的时候会抛异常，这个地方不直接抛异常，因为有些地方需要Load之后判断是否Load成功
                            Log.Warning($"Scene bundle not found: {assetBundleName}");
                            return;
                        }
                        
                        abInfo = EntityFactory.CreateWithParent<ABInfo, string, AssetBundle>(this, assetBundleName, assetBundle);
                        this.bundles[assetBundleName] = abInfo;
                    }
                }
                else
                {
                    string[] realPath = AssetDatabase.GetAssetPathsFromAssetBundle(assetBundleName);
                    foreach (string s in realPath)
                    {
                        string assetName = Path.GetFileNameWithoutExtension(s);
                        UnityEngine.Object resource = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(s);
                        AddResource(assetBundleName, assetName, resource);
                    }

                    if (realPath.Length > 0)
                    {
                        abInfo = EntityFactory.CreateWithParent<ABInfo, string, AssetBundle>(this, assetBundleName, null);
                        this.bundles[assetBundleName] = abInfo;
                        //Log.Debug($"---------------load one bundle {assetBundleName} refcount: {abInfo.RefCount}");
                    }
                    else
                    {
                        Log.Error("Bundle not exist! BundleName: " + assetBundleName);
                    }
                }
                // 编辑器模式也不能同步加载
                await TimerComponent.Instance.WaitAsync(20);
#endif
                return;
            }

            
            p = Path.Combine(PathHelper.AppHotfixResPath, assetBundleName);
            if (!File.Exists(p))
            {
                p = Path.Combine(PathHelper.AppResPath, assetBundleName);
            }

            if (!File.Exists(p))
            {
                Log.Error("Async load bundle not exist! BundleName : " + p);
                return;
            }

            using (AssetsBundleLoaderAsync assetsBundleLoaderAsync = EntityFactory.CreateWithParent<AssetsBundleLoaderAsync>(this))
            {
                assetBundle = await assetsBundleLoaderAsync.LoadAsync(p);
            }

            if (assetBundle == null)
            {
                // 获取资源的时候会抛异常，这个地方不直接抛异常，因为有些地方需要Load之后判断是否Load成功
                Log.Warning($"assets bundle not found: {assetBundleName}");
                return;
            }

            if (!assetBundle.isStreamedSceneAssetBundle)
            {
                // 异步load资源到内存cache住
                UnityEngine.Object[] assets;
                using (AssetsLoaderAsync assetsLoaderAsync = EntityFactory.CreateWithParent<AssetsLoaderAsync, AssetBundle>(this, assetBundle))
                {
                    assets = await assetsLoaderAsync.LoadAllAssetsAsync();
                }

                foreach (UnityEngine.Object asset in assets)
                {
                    AddResource(assetBundleName, asset.name, asset);
                }
            }

            abInfo = EntityFactory.CreateWithParent<ABInfo, string, AssetBundle>(this, assetBundleName, assetBundle);
            this.bundles[assetBundleName] = abInfo;
            
            //Log.Debug($"---------------load one bundle {assetBundleName} refcount: {abInfo.RefCount}");
        }

        public string DebugString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (ABInfo abInfo in this.bundles.Values)
            {
                sb.Append($"{abInfo.Name}:{abInfo.RefCount}\n");
            }

            return sb.ToString();
        }
    }
}