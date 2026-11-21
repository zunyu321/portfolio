// RocoExport —— 从《洛克王国：世界》pak 里导出指定资产为 glTF / PNG。
//
// 用法:
//   RocoExport --paks <Paks目录> --aes <64位hex> --out <输出目录>
//              [--asset <虚拟路径>]...    网格 / 骨架 / 动画 / 贴图（.uasset 可省略）
//              [--list]                    只打印每个包的导出对象，不写文件
//
// 依赖 CUE4Parse 的 rocom 分支（自定义 AES 变体 / ver12 策略 / MLE 容器）。

using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using System.Text.Json;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Serilog;

string? paks = null, aes = null, outDir = null, format = "gltf";
var targets = new List<string>();
var listOnly = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--paks": paks = args[++i]; break;
        case "--aes": aes = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--asset": targets.Add(args[++i]); break;
        case "--format": format = args[++i].ToLowerInvariant(); break;
        case "--list": listOnly = true; break;
        default:
            Console.Error.WriteLine("未知参数: " + args[i]);
            return 1;
    }
}

if (paks is null || aes is null || targets.Count == 0)
{
    Console.Error.WriteLine("用法: RocoExport --paks <dir> --aes <hex> [--out <dir>] --asset <path> [--asset ...] [--list]");
    return 1;
}

Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateLogger();
TextureDecoder.UseAssetRipperTextureDecoder = true;

var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "nrc-unpack");
Directory.CreateDirectory(cache);
try { OodleHelper.Initialize(Path.Combine(cache, OodleHelper.OodleFileName)); }
catch (Exception e) { Console.Error.WriteLine("[warn] Oodle 未初始化: " + e.Message); }
try { ZlibHelper.Initialize(Path.Combine(cache, ZlibHelper.DllName)); }
catch (Exception e) { Console.Error.WriteLine("[warn] zlib 未初始化: " + e.Message); }

var version = new VersionContainer(EGame.GAME_RocoKingdomWorld);
var provider = new DefaultFileProvider(paks, SearchOption.AllDirectories, version, StringComparer.OrdinalIgnoreCase);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(aes));
if (provider.Files.Count == 0)
{
    Console.Error.WriteLine("挂载失败：检查 paks 目录与 AES");
    return 1;
}
Console.WriteLine($"挂载 {provider.MountedVfs.Count} 个包，{provider.Files.Count} 个文件");

string? Resolve(string input)
{
    var p = input.Replace('\\', '/').TrimStart('/');
    if (provider.Files.ContainsKey(p)) return p;
    var withExt = p + ".uasset";
    if (provider.Files.ContainsKey(withExt)) return withExt;
    return null;
}

var animAssets = new List<UAnimationAsset>();
var session = new ExportSession();
var queued = 0;

foreach (var raw in targets)
{
    var path = Resolve(raw);
    if (path is null)
    {
        Console.Error.WriteLine("[miss] " + raw);
        continue;
    }

    var pkg = provider.LoadPackage(provider.Files[path]);
    var exports = pkg.GetExports().ToList();

    if (listOnly)
    {
        Console.WriteLine($"{path}  ({exports.Count} exports)");
        foreach (var e in exports) Console.WriteLine($"    {e.ExportType,-30} {e.Name}");
        continue;
    }

    foreach (var export in exports)
    {
        // 几何 / 骨架 / 动画 / 贴图；材质由 MeshExporter 按需拉取
        if (export is not (USkinnedAsset or UStaticMesh or USkeleton or UAnimationAsset or UTexture)) continue;
        try
        {
            // 动画不走 ExportSession（它不支持 glTF 动画），改为自己采样
            if (export is UAnimationAsset animAsset)
            {
                animAssets.Add(animAsset);
                continue;
            }
            session.Add(export);
            queued++;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[skip] {export.Name}: {e.Message}");
        }
    }
}

Console.WriteLine($"待导出对象 {queued} 个");
if (listOnly || (queued == 0 && animAssets.Count == 0)) return 0;

outDir ??= "export";
Directory.CreateDirectory(outDir);

var meshFormat = format switch
{
    "actorx" or "psa" => EMeshFormat.ActorX,
    "ueformat" => EMeshFormat.UEFormat,
    "usd" => EMeshFormat.USD,
    _ => EMeshFormat.Gltf2
};

var options = new ExportOptions(
    meshFormat: meshFormat,
    exportMaterials: meshFormat == EMeshFormat.Gltf2,
    exportMorphTargets: true,
    socketFormat: ESocketFormat.Bone);

var results = await session.RunAsync(outDir, options);
var ok = 0;
foreach (var r in results)
{
    if (r.Success)
    {
        ok++;
        Console.WriteLine("  OK  " + string.Join(", ", r.DiskFilePaths ?? []));
    }
    else
    {
        Console.Error.WriteLine($"  FAIL {r.ObjectPath}: {r.Error?.Message}");
    }
}
Console.WriteLine($"完成: {ok}/{results.Count}");

// ── 动画片段：按网格骨架采样，输出到 <out>/clips/<名字>.json ──
if (animAssets.Count > 0)
{
    var clipDir = Path.Combine(outDir, "clips");
    Directory.CreateDirectory(clipDir);
    foreach (var asset in animAssets)
    {
        try
        {
            var set = asset.ConvertAnims();
            var seq = set.Sequences[0];
            var refSkel = set.Skeleton.ReferenceSkeleton;
            var boneCount = refSkel.FinalRefBoneInfo.Length;
            var frames = seq.NumFrames;
            var bones = new List<object>(boneCount);
            for (var b = 0; b < boneCount; b++)
            {
                var boneName = refSkel.FinalRefBoneInfo[b].Name.Text;
                var keys = new List<object>(frames);
                for (var frame = 0; frame < frames; frame++)
                {
                    var pose = refSkel.FinalRefBonePose[b];
                    var q = pose.Rotation;
                    var p = pose.Translation;
                    var s = pose.Scale3D;
                    if (b < seq.Tracks.Count && seq.OriginalSequence.FindTrackForBoneIndex(b) >= 0)
                        seq.Tracks[b].GetBoneTransform(frame, frames, ref q, ref p, ref s);
                    keys.Add(new
                    {
                        t = Math.Round(frame / seq.FramesPerSecond, 5),
                        p = new[] { p.X * 0.01f, p.Z * 0.01f, p.Y * 0.01f },
                        q = new[] { q.X, q.Z, q.Y, -q.W },
                        s = new[] { s.X, s.Z, s.Y }
                    });
                }
                bones.Add(new { name = boneName, keys });
            }
            var clip = new { name = asset.Name, fps = seq.FramesPerSecond, frames, bones };
            var dst = Path.Combine(clipDir, asset.Name + ".json");
            File.WriteAllText(dst, JsonSerializer.Serialize(clip));

            // 同时导出这套骨架的绑定姿态（原始 UE 坐标，不做任何转换），用来和 glb 节点逐根比对
            var bind = new List<object>(boneCount);
            for (var b = 0; b < boneCount; b++)
            {
                var info = refSkel.FinalRefBoneInfo[b];
                var pose = refSkel.FinalRefBonePose[b];
                bind.Add(new
                {
                    name = info.Name.Text,
                    parent = info.ParentIndex,
                    t = new[] { pose.Translation.X, pose.Translation.Y, pose.Translation.Z },
                    r = new[] { pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W },
                    s = new[] { pose.Scale3D.X, pose.Scale3D.Y, pose.Scale3D.Z }
                });
            }
            File.WriteAllText(
                Path.Combine(clipDir, asset.Name + ".bind.json"),
                JsonSerializer.Serialize(new { name = asset.Name, bones = bind }));

            Console.WriteLine($"  动画 {asset.Name}: {frames} 帧, {boneCount} 骨骼 -> {dst}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  [anim] {asset.Name} 失败: {e.Message}");
        }
    }
}
return 0;
