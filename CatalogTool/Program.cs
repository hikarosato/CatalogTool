using AddressablesTools;
using AddressablesTools.Catalog;
using AddressablesTools.Classes;
using AssetsTools.NET;
using System.Text.Encodings.Web;
using System.Text.Json;

static bool IsUnityFS(string path)
{
    const string unityFs = "UnityFS";
    using AssetsFileReader reader = new AssetsFileReader(path);
    if (reader.BaseStream.Length < unityFs.Length)
    {
        return false;
    }

    return reader.ReadStringLength(unityFs.Length) == unityFs;
}

static void SearchAsset(string path, ContentCatalogData ccd, bool fromBundle)
{
    Console.Write("search key to find bundles of: ");
    string? search = Console.ReadLine();

    if (search == null)
    {
        return;
    }

    search = search.ToLower();

    foreach (object k in ccd.Resources.Keys)
    {
        if (k is string s && s.ToLower().Contains(search))
        {
            Console.Write(s);
            foreach (var rsrc in ccd.Resources[s])
            {
                Console.WriteLine($" ({rsrc.ProviderId})");
                if (rsrc.ProviderId == "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider")
                {
                    List<ResourceLocation> locs;
                    if (rsrc.Dependencies != null)
                    {
                        // new version
                        locs = rsrc.Dependencies;
                    }
                    else if (rsrc.DependencyKey != null)
                    {
                        // old version
                        locs = ccd.Resources[rsrc.DependencyKey];
                    }
                    else
                    {
                        continue;
                    }

                    Console.WriteLine($"  {locs[0].InternalId}");
                    if (locs.Count > 1)
                    {
                        for (int i = 1; i < locs.Count; i++)
                        {
                            Console.WriteLine($"    {locs[i].InternalId}");
                        }
                    }
                }
            }
        }
    }
}

static void PatchCrcRecursive(ResourceLocation thisRsrc, HashSet<ResourceLocation> seenRsrcs)
{
    // I think this can't happen right now, resources are duplicated every time
    if (seenRsrcs.Contains(thisRsrc))
        return;

    var data = thisRsrc.Data;
    if (data is WrappedSerializedObject { Object: AssetBundleRequestOptions abro })
    {
        abro.Crc = 0;
    }

    seenRsrcs.Add(thisRsrc);
    foreach (var childRsrc in thisRsrc.Dependencies)
    {
        PatchCrcRecursive(childRsrc, seenRsrcs);
    }
}

static void PatchCrc(string path, ContentCatalogData ccd, bool fromBundle)
{
    Console.WriteLine("patching...");

    var seenRsrcs = new HashSet<ResourceLocation>();
    foreach (var resourceList in ccd.Resources.Values)
    {
        foreach (var rsrc in resourceList)
        {
            if (rsrc.Dependencies != null)
            {
                // we just spotted a new version entry, switch to new entry parsing
                PatchCrcRecursive(rsrc, seenRsrcs);
                continue;
            }

            if (rsrc.ProviderId == "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider")
            {
                // old version
                var data = rsrc.Data;
                if (data is WrappedSerializedObject { Object: AssetBundleRequestOptions abro })
                {
                    abro.Crc = 0;
                }
            }
        }
    }

    if (fromBundle)
    {
        AddressablesCatalogFileParser.ToBundle(ccd, path, path + ".patched");
    }
    else
    {
        switch (ccd.Type)
        {
            case CatalogFileType.Json:
                File.WriteAllText(path + ".patched", AddressablesCatalogFileParser.ToJsonString(ccd));
                break;
            case CatalogFileType.Binary:
                File.WriteAllBytes(path + ".patched", AddressablesCatalogFileParser.ToBinaryData(ccd));
                break;
            default:
                return;
        }
    }

    File.Move(path, path + ".old", true);
    File.Move(path + ".patched", path, true);
}

static void ExtractAssetList(string path, ContentCatalogData ccd, bool fromBundle)
{
    Dictionary<string, string> bundleHashes = new();

    foreach (var res in ccd.Resources)
    {
        var loc = res.Value;

        if (loc[0].InternalId.StartsWith("0#") || loc[0].InternalId.Contains(".bundle"))
        {
            if (loc[0].Data is WrappedSerializedObject { Object: AssetBundleRequestOptions abro })
            {
                bundleHashes.TryAdd("0#/" + loc[0].PrimaryKey, abro.Hash);
            }
        }
    }

    Dictionary<string, string> assetList = new();

    foreach (object k in ccd.Resources.Keys)
    {
        if (k is string s && !s.Contains(".bundle") && s.Contains("/"))
        {
            foreach (var rsrc in ccd.Resources[k])
            {
                if (rsrc.ProviderId == "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider")
                {
                    List<ResourceLocation> locs;
                    if (rsrc.Dependencies != null)
                    {
                        // new version
                        locs = rsrc.Dependencies;
                    }
                    else if (rsrc.DependencyKey != null)
                    {
                        // old version
                        locs = ccd.Resources[rsrc.DependencyKey];
                    }
                    else
                    {
                        continue;
                    }

                    assetList.TryAdd(s, locs[0].PrimaryKey);
                }
            }
        }
    }

    var orderedHashes = bundleHashes.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);

    var hashesJSON = JsonSerializer.Serialize(orderedHashes, new JsonSerializerOptions() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    using (StreamWriter writer = new StreamWriter(path.Replace(".json", "").Replace(".bundle", "") + "_hash.json"))
    {
        writer.Write(hashesJSON);
    }

    var orderedList = assetList.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);

    var listJSON = JsonSerializer.Serialize(orderedList, new JsonSerializerOptions() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    using (StreamWriter writer = new StreamWriter(path.Replace(".json", "").Replace(".bundle", "") + "_list.json"))
    {
        writer.Write(listJSON);
    }

    Console.WriteLine(bundleHashes.Count);
    Console.WriteLine(assetList.Count);
}

if (args.Length == 1)
{
    var path = args[0];

    if (!File.Exists(path))
    {
        Console.WriteLine("catalog file not found!");
        return;
    }

    var fileName = Path.GetFileName(path);
    if (!string.Equals(fileName, "catalog.json", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(fileName, "catalog.bin", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("only catalog.json and catalog.bin are supported for drag&drop!");
        return;
    }

    bool fromBundle = IsUnityFS(path);

    ContentCatalogData ccd;
    CatalogFileType fileType = CatalogFileType.None;
    if (fromBundle)
    {
        ccd = AddressablesCatalogFileParser.FromBundle(path);
    }
    else
    {
        using (FileStream fs = File.OpenRead(path))
        {
            fileType = AddressablesCatalogFileParser.GetCatalogFileType(fs);
        }

        switch (fileType)
        {
            case CatalogFileType.Json:
                ccd = AddressablesCatalogFileParser.FromJsonString(File.ReadAllText(path));
                break;
            case CatalogFileType.Binary:
                ccd = AddressablesCatalogFileParser.FromBinaryData(File.ReadAllBytes(path));
                break;
            default:
                Console.WriteLine("not a valid catalog file");
                return;
        }
    }

    PatchCrc(path, ccd, fromBundle);
    return;
}

if (args.Length < 2)
{
    Console.WriteLine("need args: <mode> <file>");
    Console.WriteLine("modes: search, patch, extract");
    return;
}

if (!File.Exists(args[1]))
{
    Console.WriteLine("catalog file not found!");
    return;
}

var mode = args[0];
var catalogPath = args[1];

bool fromBundleMain = IsUnityFS(catalogPath);

ContentCatalogData ccdMain;
CatalogFileType fileTypeMain = CatalogFileType.None;
if (fromBundleMain)
{
    ccdMain = AddressablesCatalogFileParser.FromBundle(catalogPath);
}
else
{
    using (FileStream fs = File.OpenRead(catalogPath))
    {
        fileTypeMain = AddressablesCatalogFileParser.GetCatalogFileType(fs);
    }

    switch (fileTypeMain)
    {
        case CatalogFileType.Json:
            ccdMain = AddressablesCatalogFileParser.FromJsonString(File.ReadAllText(catalogPath));
            break;
        case CatalogFileType.Binary:
            ccdMain = AddressablesCatalogFileParser.FromBinaryData(File.ReadAllBytes(catalogPath));
            break;
        default:
            Console.WriteLine("not a valid catalog file");
            return;
    }
}

if (mode == "search")
{
    SearchAsset(catalogPath, ccdMain, fromBundleMain);
}
else if (mode == "patch")
{
    PatchCrc(catalogPath, ccdMain, fromBundleMain);
}
else if (mode == "extract")
{
    ExtractAssetList(catalogPath, ccdMain, fromBundleMain);
}
else
{
    Console.WriteLine("mode not supported");
}
