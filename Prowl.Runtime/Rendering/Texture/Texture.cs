// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Veldrid;
using Prowl.Echo;

using static Prowl.Runtime.Rendering.TextureUtility;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// This is the base class for all texture types and manages some of their internal workings.
/// </summary>
/// <remarks>
/// Much of this class is comprised of validations and utilities to make working with a <see cref="Veldrid.Texture"/> safer.
/// </remarks>
public abstract class Texture : EngineObject, ISerializable
{
    /// <summary>The type of this <see cref="Texture"/>, such as 1D, 2D, 3D.</summary>
    public TextureType Type => InternalTexture.Type;

    /// <summary>The format of this <see cref="Texture"/>.</summary>
    public PixelFormat Format => InternalTexture.Format;

    /// <summary>The use cases this <see cref="Texture"/> is prepared for.</summary>
    public TextureUsage Usage => InternalTexture.Usage;

    /// <summary>The mip levels of this <see cref="Texture"/>.</summary>
    public uint MipLevels => InternalTexture.MipLevels;

    /// <summary>Gets whether this <see cref="Texture"/> has mipmaps generated by calling <see cref="GenerateMipmaps"/> .</summary>
    /// <remarks>This field is disabled if a texture is recreated. Manually setting a mip level will not set this field.</remarks>
    public bool IsMipmapped { get; protected set; }

    /// <summary>Gets whether this <see cref="Texture"/> can be automatically mipmapped.</summary>
    public bool IsMipmappable => Usage.HasFlag(TextureUsage.GenerateMipmaps);

    /// <summary>The sampler for this <see cref="Texture"/></summary>
    public TextureSampler Sampler = TextureSampler.CreateLinear();

    /// <summary>The multisample count of this <see cref="Texture"/></summary>
    public TextureSampleCount SampleCount => InternalTexture.SampleCount;

    /// <summary>Gets whether this <see cref="Texture"/> owns its internal texture, or was created using an existing texture.</summary>
    public bool OwnsTexture { get; private set; }


    /// <summary>The internal <see cref="Veldrid.Texture"/> representation.</summary>
    internal Veldrid.Texture InternalTexture { get; private set; }

    private Veldrid.Texture stagingTexture = null;



    internal Texture() : base("New Texture") { }

    internal Texture(TextureDescription description) : this()
    {
        RecreateInternalTexture(description);
    }

    internal Texture(Veldrid.Texture source, TextureType type) : base(source.Name)
    {
        if (source.Type != type)
            throw new Exception($"Invalid texture type {source.Type}. Must be {type}");

        CreateFromExisting(source);
    }

    public override void OnDispose()
    {
        InternalTexture?.Dispose();

        stagingTexture?.Dispose();

        InternalTexture = null;
        stagingTexture = null;

        Sampler?.Dispose();
    }

    public void GenerateMipmaps()
    {
        if (!IsMipmappable)
            throw new InvalidOperationException($"Cannot generate mipmaps on a non-mipmappable texture. Ensure texture is created with the {TextureUsage.GenerateMipmaps} flag.");

        using CommandList commandList = Graphics.GetCommandList();
        using GraphicsFence fence = new();

        commandList.GenerateMipmaps(InternalTexture);

        Graphics.SubmitCommandList(commandList, fence);
        Graphics.WaitForFence(fence);

        IsMipmapped = true;
    }

    /// <summary>
    /// Gets the estimated memory usage in bytes of the <see cref="Texture"/>.
    /// </summary>
    public uint GetMemoryUsage()
    {
        return InternalTexture.Width * InternalTexture.Height * InternalTexture.Depth * InternalTexture.ArrayLayers * PixelFormatBytes(Format);
    }

    protected void RecreateInternalTexture(TextureDescription description)
    {
        OnDispose();

        // None of these values should ever be zero, so make sure they're all clamped to a minimum of 1.
        description.Width = Math.Max(1, description.Width);
        description.Height = Math.Max(1, description.Height);
        description.Depth = Math.Max(1, description.Depth);
        description.ArrayLayers = Math.Max(1, description.ArrayLayers);
        description.MipLevels = Math.Max(1, description.MipLevels);

        if (!IsSupportedDescription(description, out _, out Exception exception))
            throw exception;

        InternalTexture = Graphics.Factory.CreateTexture(description);
        InternalTexture.Name = Name;

        IsMipmapped = false;
        OwnsTexture = true;
    }

    protected void CreateFromExisting(Veldrid.Texture resource)
    {
        OnDispose();

        InternalTexture = resource;
        IsMipmapped = false;
        OwnsTexture = false;
    }

    protected unsafe void InternalSetDataPtr(void* data, Vector3Int rectPos, Vector3Int rectSize, uint layer, uint mipLevel)
    {
        if (!OwnsTexture)
            throw new Exception("Cannot modify texture created from external texture object.");

        ValidateRectOperation(rectPos, rectSize, layer, mipLevel);

        uint mipWidth = GetMipDimension(InternalTexture.Width, mipLevel);
        uint mipHeight = GetMipDimension(InternalTexture.Height, mipLevel);
        uint mipDepth = GetMipDimension(InternalTexture.Depth, mipLevel);

        uint mipLevelSize = mipWidth * mipHeight * mipDepth * PixelFormatBytes(Format);

        EnsureStagingTexture();
        Graphics.Device.UpdateTexture(stagingTexture, (IntPtr)data, mipLevelSize, (uint)rectPos.x, (uint)rectPos.y, (uint)rectPos.z, (uint)rectSize.x, (uint)rectSize.y, (uint)rectSize.z, mipLevel, layer);

        if (stagingTexture != InternalTexture)
            InternalCopyTexture(stagingTexture, InternalTexture, mipLevel, layer);
    }

    protected unsafe void InternalSetData<T>(Span<T> data, Vector3Int rectPos, Vector3Int rectSize, uint layer, uint mipLevel) where T : unmanaged
    {
        if (data.Length * sizeof(T) < rectSize.x * rectSize.y * rectSize.z)
            throw new ArgumentException("Not enough pixel data", nameof(data));

        fixed (void* ptr = data)
            InternalSetDataPtr(ptr, rectPos, rectSize, layer, mipLevel);
    }

    protected unsafe void InternalCopyDataPtr(void* dataPtr, uint dataSize, out uint rowPitch, out uint depthPitch, uint arrayLayer, uint mipLevel)
    {
        EnsureStagingTexture();

        if (stagingTexture != InternalTexture)
            InternalCopyTexture(InternalTexture, stagingTexture, mipLevel, arrayLayer);

        uint subresource = (MipLevels * arrayLayer) + mipLevel;

        MappedResource resource = Graphics.Device.Map(stagingTexture, MapMode.Read, subresource);

        rowPitch = resource.RowPitch;
        depthPitch = resource.DepthPitch;

        Buffer.MemoryCopy((void*)resource.Data, dataPtr, dataSize, Math.Min(resource.SizeInBytes, dataSize));

        Graphics.Device.Unmap(stagingTexture, subresource);
    }

    protected unsafe void InternalCopyData<T>(Span<T> data, uint arrayLayer, uint mipLevel) where T : unmanaged
    {
        EnsureStagingTexture();

        if (stagingTexture != InternalTexture)
            InternalCopyTexture(InternalTexture, stagingTexture, mipLevel, arrayLayer);

        uint subresource = (MipLevels * arrayLayer) + mipLevel;

        MappedResource resource = Graphics.Device.Map(stagingTexture, MapMode.Read, subresource);

        if ((data.Length * sizeof(T)) < resource.SizeInBytes)
            throw new ArgumentException("Insufficient space to store the requested pixel data", nameof(data));

        fixed (void* ptr = data)
            Buffer.MemoryCopy((void*)resource.Data, ptr, data.Length * sizeof(T), resource.SizeInBytes);

        Graphics.Device.Unmap(stagingTexture, subresource);
    }

    protected unsafe T InternalCopyPixel<T>(Vector3Int pixelPosition, uint arrayLayer, uint mipLevel) where T : unmanaged
    {
        ValidateRectOperation(pixelPosition, new Vector3Int(1, 1, 1), arrayLayer, mipLevel);

        EnsureStagingTexture();

        if (stagingTexture != InternalTexture)
            InternalCopyTexture(InternalTexture, stagingTexture, mipLevel, arrayLayer);

        uint subresource = (MipLevels * arrayLayer) + mipLevel;

        MappedResource resource = Graphics.Device.Map(stagingTexture, MapMode.Read, subresource);

        uint width = GetMipDimension(InternalTexture.Width, mipLevel);
        uint height = GetMipDimension(InternalTexture.Height, mipLevel);
        uint depth = GetMipDimension(InternalTexture.Depth, mipLevel);

        double pX = (double)pixelPosition.x / InternalTexture.Width;
        double pY = (double)pixelPosition.y / InternalTexture.Height;
        double pZ = (double)pixelPosition.z / InternalTexture.Depth;

        uint pixelX = (uint)Math.Floor(pX * width);
        uint pixelY = (uint)Math.Floor(pY * height);
        uint pixelZ = (uint)Math.Floor(pZ * depth);

        uint offset = pixelX + (pixelY * width) + (pixelZ * width * height);

        T data = default;

        long copySize = Math.Min(sizeof(T), PixelFormatBytes(InternalTexture.Format));
        Buffer.MemoryCopy((void*)(resource.Data + (offset * sizeof(T))), Unsafe.AsPointer(ref data), sizeof(T), copySize);

        Graphics.Device.Unmap(stagingTexture, subresource);

        return data;
    }

    private static void InternalCopyTexture(Veldrid.Texture source, Veldrid.Texture destination, uint mipLevel, uint arrayLayer)
    {
        using CommandList commandList = Graphics.GetCommandList();
        using GraphicsFence fence = new();

        commandList.CopyTexture(source, destination, mipLevel, arrayLayer);

        Graphics.SubmitCommandList(commandList, fence);
        Graphics.WaitForFence(fence);
    }

    // Ensure that a CPU-accessible staging texture matching the internal one exists
    // If the internal texture is already a staging texture, uses itself.
    private void EnsureStagingTexture()
    {
        if (InternalTexture.Usage.HasFlag(TextureUsage.Staging))
        {
            stagingTexture = InternalTexture;
            return;
        }

        if (stagingTexture != null &&
            stagingTexture.Width == InternalTexture.Width &&
            stagingTexture.Height == InternalTexture.Height &&
            stagingTexture.Depth == InternalTexture.Depth &&
            stagingTexture.ArrayLayers == InternalTexture.ArrayLayers &&
            stagingTexture.Type == InternalTexture.Type &&
            stagingTexture.MipLevels == InternalTexture.MipLevels &&
            stagingTexture.Format == InternalTexture.Format)
            return;

        TextureDescription description = new()
        {
            Width = InternalTexture.Width,
            Height = InternalTexture.Height,
            Depth = InternalTexture.Depth,
            ArrayLayers = InternalTexture.ArrayLayers,
            Type = Type,
            MipLevels = MipLevels,
            Usage = TextureUsage.Staging,
            Format = Format,
            SampleCount = TextureSampleCount.Count1,
        };

        stagingTexture = Graphics.Factory.CreateTexture(description);

        return;
    }

    private void ValidateRectOperation(Vector3Int rect, Vector3Int size, uint layer, uint mipLevel)
    {
        if (rect.x < 0 || rect.x >= InternalTexture.Width)
            throw new ArgumentOutOfRangeException(nameof(rect), rect.x, "Rect X must be in the range [0, " + InternalTexture.Width + "]");

        if (rect.y < 0 || rect.y >= InternalTexture.Height)
            throw new ArgumentOutOfRangeException(nameof(rect), rect.y, "Rect Y must be in the range [0, " + InternalTexture.Height + "]");

        if (rect.z < 0 || rect.z >= InternalTexture.Depth)
            throw new ArgumentOutOfRangeException(nameof(rect), rect.z, "Rect Z must be in the range [0, " + InternalTexture.Depth + "]");

        if (size.x <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size.x, "Rect width must be greater than 0");

        if (size.y <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size.y, "Rect height must be greater than 0");

        if (size.z <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size.z, "Rect depth must be greater than 0");

        if (size.x > InternalTexture.Width - rect.x || size.y > InternalTexture.Height - rect.y || size.z > InternalTexture.Depth - rect.z)
            throw new ArgumentOutOfRangeException("Specified area is outside of the texture's storage");

        if (layer >= InternalTexture.ArrayLayers)
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "Array layer must be in the range [0, " + InternalTexture.ArrayLayers + "]");

        if (mipLevel >= InternalTexture.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel, "Mip level must be in the range [0, " + InternalTexture.MipLevels + "]");
    }

    public override bool Equals(object? obj)
    {
        if (obj is not Texture texture)
            return false;

        return Equals(texture, true);
    }

    public override int GetHashCode()
        => base.GetHashCode();

    public bool Equals(Texture other, bool compareMS = true)
    {
        if (other == null ||
            other.InternalTexture.Width != InternalTexture.Width ||
            other.InternalTexture.Height != InternalTexture.Height ||
            other.InternalTexture.Depth != InternalTexture.Depth ||
            other.InternalTexture.ArrayLayers != InternalTexture.ArrayLayers ||
            other.Type != InternalTexture.Type ||
            other.MipLevels != InternalTexture.MipLevels ||
            other.Format != InternalTexture.Format ||
            (compareMS && other.InternalTexture.SampleCount != InternalTexture.SampleCount)
           )
            return true;

        return false;
    }


    public abstract void Serialize(ref EchoObject compoundTag, SerializationContext ctx);

    public abstract void Deserialize(EchoObject value, SerializationContext ctx);
}
