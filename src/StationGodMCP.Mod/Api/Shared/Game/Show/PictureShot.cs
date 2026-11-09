#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Pictures;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace StationGodMCP.Api.Shared.Game.Show;

/// <summary>What show picture was asked for, its options read and its path checked.</summary>
internal sealed class PictureRequest
{
    internal PictureRequest(Thing target, string path, bool overwrite, int width, int height, double yaw, double pitch,
        double? distance, double fov, bool neutral)
    {
        Target = target;
        Path = path;
        Overwrite = overwrite;
        Width = width;
        Height = height;
        Yaw = yaw;
        Pitch = pitch;
        Distance = distance;
        Fov = fov;
        Neutral = neutral;
    }

    internal Thing Target { get; }

    internal string Path { get; }

    internal bool Overwrite { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal double Yaw { get; }

    internal double Pitch { get; }

    internal double? Distance { get; }

    internal double Fov { get; }

    internal bool Neutral { get; }
}

/// <summary>
/// One show picture, over a few frames (scheduling.md, Work over frames): the first step places a temporary camera,
/// renders it into a render texture (the scene's lights swapped for neutral ones inside try/finally when asked) and
/// asks the GPU for the pixels without a stall (AsyncGPUReadback into a buffer of its own; the texture is freed at once);
/// later steps wait for them, then a pool thread makes them opaque, encodes the PNG and writes a temporary file
/// (PictureWrite); the last step moves it to its name and answers. Dropped (Dispose), nothing is left: no texture, no
/// buffer, no file, and the gate is free. The player's own camera, view and HUD are
/// never touched: the HUD's overlay canvases draw only to the screen, and the camera sees the player's body through the
/// third-person layers (CameraController._thirdPersonMask; the local player's worn things sit on PlayerInvisible and
/// its suit on PlayerImmune, which the first-person mask leaves out). Main thread, but for the pool thread's encode.
/// </summary>
internal sealed class PictureShot : IPendingResult, IDisposable
{
    /// <summary>FirstPersonHelmetOverlay.FirstPersonLayer: the helmet drawn over the first-person view.</summary>
    private const int FirstPersonLayer = 26;

    private const float NearClip = 0.05f;

    private readonly PictureRequest _request;
    private readonly List<string> _notes = new List<string>();
    private RenderTexture? _texture;
    private NativeArray<byte> _pixels;
    private AsyncGPUReadbackRequest _readback;
    private bool _readbackPending;
    private PictureWrite? _write;
    private PicturePose? _pose;
    private PictureTargetView? _targetView;
    private string? _occludedBy;
    private double _mainThreadMs;
    private long _bytes;
    private Exception? _failure;
    private bool _done;
    private bool _disposed;

    private PictureShot(PictureRequest request)
    {
        _request = request;
    }

    /// <summary>Starts the picture: the gate taken, the render done now; Pending until the file is written.</summary>
    internal static PictureShot Start(PictureRequest request)
    {
        PictureShot shot = new PictureShot(request);
        if (!PictureGate.Main.TryTake(shot, Time.realtimeSinceStartup))
        {
            throw ApiErrors.Refused(PictureGate.Busy,
                "show picture: another picture is being taken; call again once its reply has come.");
        }

        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            shot.Render();
        }
        catch
        {
            shot.Dispose();
            throw;
        }
        finally
        {
            shot._mainThreadMs += watch.Elapsed.TotalMilliseconds;
        }

        return shot;
    }

    public bool Step(double untilMs)
    {
        if (_done)
        {
            return true;
        }

        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            if (_readbackPending)
            {
                if (!_readback.done)
                {
                    return false;
                }

                _readbackPending = false;
                if (_readback.hasError)
                {
                    throw new InvalidOperationException("the GPU readback failed.");
                }

                StartWrite();
                return false;
            }

            if (_write != null && !_write.IsDone)
            {
                return false;
            }

            _bytes = _write != null ? _write.Commit() : throw new InvalidOperationException("the picture was not written.");
        }
        catch (Exception failure)
        {
            _failure = failure;
        }
        finally
        {
            _mainThreadMs += watch.Elapsed.TotalMilliseconds;
        }

        Finish();
        return true;
    }

    public object Result()
    {
        switch (_failure)
        {
            case null:
                break;
            case ApiException refusal:
                throw refusal;
            case PictureWrite.FileExistsException:
                throw ApiErrors.Refused("file_exists", $"show picture: {_request.Path} already exists; give overwrite: true.");
            default:
                throw ApiErrors.Refused("render_failed", $"show picture: {_failure.Message}");
        }

        return new PictureView(_request.Path, _request.Width, _request.Height, _bytes, _targetView!,
            new PictureCameraView(_pose!), _request.Neutral ? "neutral" : "scene", _occludedBy, _mainThreadMs, _notes);
    }

    /// <summary>
    /// Lets everything go: the render texture, a readback still in flight (waited for, then its buffer freed), the
    /// file not yet committed (never written, or deleted), and the gate. A dropped call (DeferredCalls) ends here.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            FreeTexture();
            if (_readbackPending)
            {
                // The GPU still writes into the buffer: it may be freed only once the request is done.
                AsyncGPUReadback.WaitAllRequests();
                _readbackPending = false;
            }

            if (_write != null)
            {
                _write.Dispose();
            }
            else if (_pixels.IsCreated)
            {
                _pixels.Dispose();
            }
        }
        finally
        {
            PictureGate.Main.Release(this);
        }
    }

    private void Finish()
    {
        _done = true;
        Dispose();
    }

    private void FreeTexture()
    {
        if (_texture != null)
        {
            _texture.Release();
            Object.Destroy(_texture);
            _texture = null;
        }
    }

    private void Render()
    {
        Thing target = _request.Target;
        (Vector3 centre, float radius) = Frame(target);
        Vec3 front = Front(target);
        double aspect = (double)_request.Width / _request.Height;
        PicturePose pose = PicturePose.Of(new Vec3(centre.x, centre.y, centre.z), front,
            radius, _request.Yaw, _request.Pitch, _request.Distance, _request.Fov, aspect);
        _pose = pose;
        _targetView = new PictureTargetView(target.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.PrefabName, new Vec3(centre.x, centre.y, centre.z), radius, front);
        Vector3 position = new Vector3((float)pose.Position.X, (float)pose.Position.Y, (float)pose.Position.Z);
        Vector3 direction = new Vector3((float)pose.Direction.X, (float)pose.Direction.Y, (float)pose.Direction.Z);
        _occludedBy = Occluder(position, direction, (float)pose.Distance, target);

        Camera main = CameraController.CurrentCamera;
        // Never more anti-aliasing than the player's own view has.
        int msaa = main != null && main.allowMSAA ? Mathf.Clamp(QualitySettings.antiAliasing, 1, 8) : 1;
        _texture = new RenderTexture(_request.Width, _request.Height, 24, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB)
        {
            antiAliasing = msaa,
            name = "StationGod picture",
        };
        _texture.Create();

        GameObject holder = new GameObject("StationGod picture camera");
        try
        {
            Camera camera = holder.AddComponent<Camera>();
            camera.enabled = false;
            if (main != null)
            {
                camera.CopyFrom(main);
            }
            else
            {
                _notes.Add("The game has no main camera; the picture camera uses Unity's defaults (skybox, far 1000 m).");
            }

            camera.cullingMask = CullingMask(main);
            camera.fieldOfView = (float)_request.Fov;
            camera.nearClipPlane = NearClip;
            camera.aspect = (float)aspect;
            camera.targetTexture = _texture;
            camera.allowHDR = false;
            holder.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, Vector3.up));

            SceneSwap.Run(swap =>
            {
                if (_request.Neutral)
                {
                    NeutralLight(swap, holder.transform.rotation);
                }
            }, camera.Render);
            camera.targetTexture = null;
        }
        finally
        {
            Object.Destroy(holder);
        }

        _pixels = new NativeArray<byte>(_request.Width * _request.Height * 4, Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        if (SystemInfo.supportsAsyncGPUReadback)
        {
            _readback = AsyncGPUReadback.RequestIntoNativeArray(ref _pixels, _texture, 0, TextureFormat.RGBA32);
            _readbackPending = true;
            // The GPU's copy is queued: the texture is not needed any more.
            FreeTexture();
            return;
        }

        ReadNow(_texture, _pixels);
        FreeTexture();
        _notes.Add("This GPU cannot read back without a stall; the pixels were read at once.");
        StartWrite();
    }

    /// <summary>
    /// Hands the read pixels to a pool thread: made opaque, encoded and written to a temporary file there (so the main
    /// thread copies nothing out of the buffer); the buffer is freed there too.
    /// </summary>
    private void StartWrite()
    {
        NativeArray<byte> pixels = _pixels;
        _pixels = default;
        uint width = (uint)_request.Width;
        uint height = (uint)_request.Height;
        _write = new PictureWrite(_request.Path, _request.Overwrite, token =>
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }

                using (NativeArray<byte> png = ImageConversion.EncodeNativeArrayToPNG(pixels,
                           GraphicsFormat.R8G8B8A8_UNorm, width, height))
                {
                    return png.ToArray();
                }
            }
            finally
            {
                pixels.Dispose();
            }
        });
    }

    private static void ReadNow(RenderTexture texture, NativeArray<byte> into)
    {
        RenderTexture before = RenderTexture.active;
        Texture2D read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
        try
        {
            RenderTexture.active = texture;
            read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            NativeArray<byte>.Copy(read.GetRawTextureData<byte>(), into, into.Length);
        }
        finally
        {
            RenderTexture.active = before;
            Object.Destroy(read);
        }
    }

    /// <summary>
    /// The third-person mask (the player's body and worn things in view), less the first-person helmet overlay's layer;
    /// built from the main camera's mask and the player layers, with a note, when a patch renamed the field.
    /// </summary>
    private int CullingMask(Camera? main)
    {
        int mask;
        if (GameMembers.CameraThirdPersonMask.TryResolve() && CameraController.Instance != null)
        {
            mask = ((LayerMask)GameMembers.CameraThirdPersonMask.GetValue(CameraController.Instance)).value;
        }
        else
        {
            mask = (main != null ? main.cullingMask : ~0) | LayerBit(Layers.Player) | LayerBit(Layers.PlayerInvisible) |
                   LayerBit(Layers.PlayerImmune);
            _notes.Add("CameraController._thirdPersonMask was not found (a game patch?); the picture uses the main " +
                       "camera's layers plus Player, PlayerInvisible and PlayerImmune.");
        }

        return mask & ~(1 << FirstPersonLayer);
    }

    private static int LayerBit(LayerMask layer) => layer.value >= 0 && layer.value < 32 ? 1 << layer.value : 0;

    /// <summary>
    /// For this render only: every enabled directional light off, one white key light from above and behind the
    /// camera's right shoulder, and a flat grey ambient. Point and spot lights stay as they are.
    /// </summary>
    private void NeutralLight(SceneSwap swap, Quaternion cameraRotation)
    {
        foreach (Light light in Object.FindObjectsOfType<Light>())
        {
            if (light.enabled && light.type == LightType.Directional)
            {
                Light held = light;
                swap.Set(() => held.enabled, value => held.enabled = value, false);
            }
        }

        GameObject keyHolder = new GameObject("StationGod picture key light");
        swap.Hold(() => Object.DestroyImmediate(keyHolder));
        keyHolder.transform.rotation = cameraRotation * Quaternion.Euler(25f, -20f, 0f);
        Light key = keyHolder.AddComponent<Light>();
        key.type = LightType.Directional;
        key.color = Color.white;
        key.intensity = 1f;
        key.shadows = LightShadows.None;
        // The probe first, so it is put back last: Skybox mode (the game's) does not rebuild it on its own.
        SphericalHarmonicsL2 probe = RenderSettings.ambientProbe;
        swap.Hold(() => RenderSettings.ambientProbe = probe);
        swap.Set(() => RenderSettings.ambientMode, value => RenderSettings.ambientMode = value, AmbientMode.Flat);
        swap.Set(() => RenderSettings.ambientLight, value => RenderSettings.ambientLight = value,
            new Color(0.4f, 0.4f, 0.4f, 1f));
        _notes.Add("Neutral lighting: the scene's directional lights were off for this picture only; point and spot " +
                   "lights stayed.");
    }

    /// <summary>
    /// The thing's centre and bounding radius: the union of its visible renderers' bounds (its worn and held things
    /// with it), else its own bounds.
    /// </summary>
    private static (Vector3 Centre, float Radius) Frame(Thing thing)
    {
        bool any = false;
        Bounds union = default;
        foreach (Renderer renderer in thing.GetComponentsInChildren<Renderer>())
        {
            if (!renderer.enabled || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) ||
                renderer.gameObject.layer == FirstPersonLayer)
            {
                continue;
            }

            if (any)
            {
                union.Encapsulate(renderer.bounds);
            }
            else
            {
                union = renderer.bounds;
                any = true;
            }
        }

        if (any && union.extents.sqrMagnitude > 1e-6f)
        {
            return (union.center, union.extents.magnitude);
        }

        float radius = thing.Bounds.extents.magnitude;
        return (thing.CenterPosition, radius > 0.01f ? radius : 0.5f);
    }

    /// <summary>The thing's level front: an entity's body (CharacterRotationY), else its own transform (PictureFront).</summary>
    private static Vec3 Front(Thing thing)
    {
        Transform? body = thing is Entity entity ? entity.CharacterRotationY : null;
        return PictureFront.Of(body != null ? Vec(body.forward) : null,
            thing.ThingTransform != null ? Vec(thing.ThingTransform.forward) : null);
    }

    private static Vec3? Vec(Vector3 v) => new Vec3(v.x, v.y, v.z);

    /// <summary>The first thing (or "terrain") a ray from the camera to the centre meets, when not the target or held by it.</summary>
    private static string? Occluder(Vector3 from, Vector3 direction, float distance, Thing target)
    {
        if (!Physics.Raycast(from, direction, out RaycastHit hit, distance, ~0, QueryTriggerInteraction.Ignore))
        {
            return null;
        }

        Thing? met = hit.collider != null ? hit.collider.GetComponentInParent<Thing>() : null;
        if (met == null)
        {
            return "terrain";
        }

        for (Thing? holder = met; holder != null; holder = HolderOf(holder))
        {
            if (holder == target)
            {
                return null;
            }
        }

        return met.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The thing whose slot holds this one (a worn suit's wearer), else null.</summary>
    private static Thing? HolderOf(Thing thing)
    {
        Slot? slot = thing is DynamicThing dynamic ? dynamic.ParentSlot : null;
        return slot != null ? slot.Parent : null;
    }
}
