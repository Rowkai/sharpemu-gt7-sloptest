// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Sockets;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

/// <summary>
/// recv/recvfrom carry the datagram into guest memory and report the byte count
/// in Rax. The would-block answer matters as much as the data one: a title
/// polling a non-blocking socket loops on SCE_NET_ERROR_EWOULDBLOCK and has no
/// path for the "export missing" error an unresolved import would return.
/// </summary>
public sealed class NetReceiveTests
{
    private const ulong BufferAddress = 0x1_0000_0100;
    private const ulong SenderAddress = 0x1_0000_0200;
    private const ulong SenderLengthAddress = 0x1_0000_0280;
    private const int MsgDontWait = 0x80;
    private const int NetErrorWouldBlock = unchecked((int)0x80410123);
    private const int NetErrorBadFileDescriptor = unchecked((int)0x80410109);

    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    [Fact]
    public void Recvfrom_CopiesTheDatagramAndTheSenderIntoGuestMemory()
    {
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var bound = (IPEndPoint)receiver.LocalEndPoint!;

        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        sender.SendTo(payload, bound);

        Assert.True(NetExports.TryAllocateSocketDescriptor(receiver, out var descriptor));
        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)descriptor);
            _ctx[CpuRegister.Rsi] = BufferAddress;
            _ctx[CpuRegister.Rdx] = 64;
            _ctx[CpuRegister.Rcx] = 0;
            _ctx[CpuRegister.R8] = SenderAddress;
            _ctx[CpuRegister.R9] = SenderLengthAddress;

            Assert.Equal(0, NetExports.NetRecvfrom(_ctx));
            Assert.Equal((ulong)payload.Length, _ctx[CpuRegister.Rax]);

            var received = new byte[payload.Length];
            Assert.True(_ctx.Memory.TryRead(BufferAddress, received));
            Assert.Equal(payload, received);

            // sockaddr_in: len, family 2 (AF_INET), then the port in network order.
            var address = new byte[8];
            Assert.True(_ctx.Memory.TryRead(SenderAddress, address));
            Assert.Equal(16, address[0]);
            Assert.Equal(2, address[1]);
            Assert.Equal(IPAddress.Loopback.GetAddressBytes(), address[4..8]);
        }
        finally
        {
            NetExports.ReleaseSocketDescriptor(descriptor);
        }
    }

    [Fact]
    public void Recv_WithDontWaitAndNoData_ReportsWouldBlockRatherThanBlocking()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        Assert.True(NetExports.TryAllocateSocketDescriptor(socket, out var descriptor));
        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)descriptor);
            _ctx[CpuRegister.Rsi] = BufferAddress;
            _ctx[CpuRegister.Rdx] = 64;
            _ctx[CpuRegister.Rcx] = MsgDontWait;

            Assert.Equal(NetErrorWouldBlock, NetExports.NetRecv(_ctx));
        }
        finally
        {
            NetExports.ReleaseSocketDescriptor(descriptor);
        }
    }

    [Fact]
    public void Recv_OnAnUnknownDescriptor_ReportsBadFileDescriptor()
    {
        _ctx[CpuRegister.Rdi] = 999;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 64;
        _ctx[CpuRegister.Rcx] = 0;

        Assert.Equal(NetErrorBadFileDescriptor, NetExports.NetRecv(_ctx));
    }
}
