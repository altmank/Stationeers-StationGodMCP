#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Pictures;

namespace StationGodMCP.Api.Views;

/// <summary>show picture: the file written, its size, what was pictured and the camera that pictured it.</summary>
internal sealed class PictureView
{
    internal PictureView(string path, int width, int height, long bytes, PictureTargetView target,
        PictureCameraView camera, string lighting, string? occludedBy, double renderMs, List<string> notes)
    {
        Path = path;
        Width = width;
        Height = height;
        Bytes = bytes;
        Target = target;
        Camera = camera;
        Lighting = lighting;
        OccludedBy = occludedBy;
        RenderMs = Math.Round(renderMs, 2);
        Notes = notes;
    }

    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    public long Bytes { get; }

    public PictureTargetView Target { get; }

    public PictureCameraView Camera { get; }

    /// <summary>scene or neutral.</summary>
    public string Lighting { get; }

    /// <summary>The first thing a ray from the camera to the centre meets, when not the pictured thing; else null.</summary>
    public string? OccludedBy { get; }

    /// <summary>
    /// The main-thread milliseconds the picture took, every step together: framing, the camera pass with its scene
    /// changes, the readback's request and the file's move (the encode and write run on a pool thread).
    /// </summary>
    public double RenderMs { get; }

    public List<string> Notes { get; }
}

/// <summary>The pictured thing: its id and prefab, its centre and bounding radius, and the level front yaw 0 stands on.</summary>
internal sealed class PictureTargetView
{
    internal PictureTargetView(string id, string prefab, Vec3 centre, double radius, Vec3 front)
    {
        Id = id;
        Prefab = prefab;
        Centre = new PositionView(centre.X, centre.Y, centre.Z);
        RadiusM = Math.Round(radius, 2);
        Front = new PictureFrontView(front);
    }

    public string Id { get; }

    public string Prefab { get; }

    public PositionView Centre { get; }

    public double RadiusM { get; }

    public PictureFrontView Front { get; }
}

/// <summary>The level unit vector of the pictured thing's front, in world axes.</summary>
internal sealed class PictureFrontView
{
    internal PictureFrontView(Vec3 front)
    {
        X = Math.Round(front.X, 3);
        Z = Math.Round(front.Z, 3);
    }

    public double X { get; }

    public double Z { get; }
}

/// <summary>The camera as used: where it stood, what it looked at, its yaw, pitch, field of view and distance.</summary>
internal sealed class PictureCameraView
{
    internal PictureCameraView(PicturePose pose)
    {
        Position = new PositionView(pose.Position.X, pose.Position.Y, pose.Position.Z);
        LookAt = new PositionView(pose.LookAt.X, pose.LookAt.Y, pose.LookAt.Z);
        Yaw = Math.Round(pose.Yaw, 2);
        Pitch = Math.Round(pose.Pitch, 2);
        Fov = Math.Round(pose.Fov, 2);
        DistanceM = Math.Round(pose.Distance, 2);
    }

    public PositionView Position { get; }

    public PositionView LookAt { get; }

    public double Yaw { get; }

    public double Pitch { get; }

    public double Fov { get; }

    public double DistanceM { get; }
}
