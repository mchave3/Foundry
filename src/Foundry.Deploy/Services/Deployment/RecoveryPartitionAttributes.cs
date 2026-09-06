// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>Preserves GPT metadata while setting both required recovery bits on one verified volume handle.</summary>
internal static class RecoveryPartitionAttributes
{
    internal const ulong RequiredAttributes = 0x8000000000000001UL;
    private static readonly Guid RecoveryType = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    public static void Apply(DeploymentPartitionIdentity expected)
    {
        expected.Validate();
        using SafeFileHandle handle = CreateFileW(expected.VolumeRoot.TrimEnd('\\'), 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the confirmed recovery volume.");
        Apply(expected, () => Read(handle), buffer =>
        {
            if (!DeviceIoControl(handle, 0x0007C04C, buffer, (uint)buffer.Length, null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to set recovery partition attributes.");
        });
    }

    internal static void Apply(DeploymentPartitionIdentity expected, Func<byte[]> read, Action<byte[]> write)
    {
        byte[] before = read();
        Validate(before, expected);
        byte[] input = new byte[120];
        BinaryPrimitives.WriteInt32LittleEndian(input, 1);
        before.AsSpan(32, 112).CopyTo(input.AsSpan(8));
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(40), RequiredAttributes);
        write(input);
        byte[] after = read();
        Validate(after, expected);
        if (BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan(64)) != RequiredAttributes ||
            !before.AsSpan(72, 72).SequenceEqual(after.AsSpan(72, 72)))
            throw new InvalidOperationException("Recovery partition attribute readback did not match.");
    }

    private static void Validate(byte[] data, DeploymentPartitionIdentity expected)
    {
        if (data.Length != 144 || BinaryPrimitives.ReadInt32LittleEndian(data) != 1 ||
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)) != expected.Offset ||
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)) != expected.Size || expected.Size != 5120UL * 1024 * 1024 ||
            new Guid(data.AsSpan(32, 16)) != RecoveryType || new Guid(data.AsSpan(48, 16)) != expected.PartitionId || expected.PartitionId == Guid.Empty)
            throw new InvalidOperationException("The recovery volume does not match the confirmed partition.");
    }

    private static byte[] Read(SafeFileHandle handle)
    {
        byte[] output = new byte[144];
        if (!DeviceIoControl(handle, 0x00070048, null, 0, output, (uint)output.Length, out uint returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect the recovery partition.");
        if (returned != output.Length) throw new InvalidOperationException("Recovery partition inspection was incomplete.");
        return output;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        [In] byte[]? input, uint inputSize, [Out] byte[]? output, uint outputSize,
        out uint bytesReturned, IntPtr overlapped);
}
