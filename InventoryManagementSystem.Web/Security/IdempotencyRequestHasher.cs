using System.Security.Cryptography;
using System.Text.Json;

namespace InventoryManagementSystem.Web.Security;

public static class IdempotencyRequestHasher
{
    public static string Compute<T>(T request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
}
