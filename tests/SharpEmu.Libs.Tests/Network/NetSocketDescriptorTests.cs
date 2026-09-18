// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq;
using System.Net.Sockets;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

/// <summary>
/// Socket descriptors are POSIX file descriptors. A title that calls select()
/// indexes a FD_SETSIZE-bit stack bitmap with the descriptor itself, so a
/// descriptor at or past 1024 corrupts the caller's stack rather than failing
/// a call. The kernel keeps descriptors small by reusing the lowest free one.
/// </summary>
public sealed class NetSocketDescriptorTests
{
    [Fact]
    public void CreatedSockets_GetSmallDescriptorsThatFitAnFdSet()
    {
        var sockets = new List<Socket>();
        var descriptors = new List<int>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                sockets.Add(socket);
                Assert.True(NetExports.TryAllocateSocketDescriptor(socket, out var descriptor));
                descriptors.Add(descriptor);
            }

            Assert.All(descriptors, d => Assert.InRange(d, 3, 1023));
            Assert.Equal(descriptors.Count, descriptors.Distinct().Count());
        }
        finally
        {
            foreach (var descriptor in descriptors)
            {
                NetExports.ReleaseSocketDescriptor(descriptor);
            }

            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
    }

    [Fact]
    public void AClosedDescriptor_IsHandedOutAgain()
    {
        using var first = new Socket(SocketType.Dgram, ProtocolType.Udp);
        using var second = new Socket(SocketType.Dgram, ProtocolType.Udp);

        Assert.True(NetExports.TryAllocateSocketDescriptor(first, out var firstDescriptor));
        NetExports.ReleaseSocketDescriptor(firstDescriptor);
        Assert.True(NetExports.TryAllocateSocketDescriptor(second, out var secondDescriptor));

        Assert.Equal(firstDescriptor, secondDescriptor);
        NetExports.ReleaseSocketDescriptor(secondDescriptor);
    }
}
