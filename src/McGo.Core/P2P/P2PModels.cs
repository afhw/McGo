using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace McGo.Core.P2P;

public sealed record P2PTunnelConfig(string RelayHost, int RelayPort, string Room, string Secret, int LocalPort, int RemotePort);

public static class P2PUtilities
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string GenerateRoomCode(int length = 8)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(value => Alphabet[value % Alphabet.Length]).ToArray());
    }

    public static string CleanRoom(string? value)
        => new((value ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).Take(16).ToArray());

    public static int CleanPort(object? value, int fallback = 25565)
        => int.TryParse(value?.ToString(), out var port) && port is > 0 and <= 65535 ? port : fallback;

    public static int PickLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
