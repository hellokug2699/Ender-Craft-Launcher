// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Auth;
using Ecl.Wardrobe;
using Ecl.Wardrobe.Appearance;
using Ecl.Wardrobe.Authlib;
using Ecl.Wardrobe.Png;
using Xunit;

namespace Ecl.Tests.Wardrobe;

/// <summary>ADR 0037 / 0038：本地衣柜、PNG 校验、Provider 能力与 Authlib 材质协议。</summary>
public sealed class WardrobeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ecl-wardrobe-tests", Guid.NewGuid().ToString("N"));

    public WardrobeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PngValidatorAccepts64x64AndRejectsMalformedInput()
    {
        var png = CreatePng(64, 64, transparentArmOuterColumns: false);
        var valid = PngValidator.Validate(png);
        Assert.True(valid.IsValid, valid.Message);
        Assert.Equal(64, valid.Width);
        Assert.Equal(64, valid.Height);
        Assert.Equal(SkinModel.Classic, SkinModelHeuristic.Infer(valid.Image).Model);

        var badSignature = (byte[])png.Clone();
        badSignature[1] ^= 0xFF;
        Assert.Equal(PngValidationStatus.InvalidSignature, PngValidator.Validate(badSignature).Status);

        var corrupted = (byte[])png.Clone();
        corrupted[^1] ^= 0xFF;
        Assert.Equal(PngValidationStatus.InvalidChunk, PngValidator.Validate(corrupted).Status);

        var tooWide = CreatePng(256, 8);
        Assert.Equal(PngValidationStatus.DimensionTooLarge, PngValidator.Validate(tooWide).Status);

        Assert.Equal(PngValidationStatus.Empty, PngValidator.Validate(ReadOnlySpan<byte>.Empty).Status);
    }

    [Fact]
    public void SkinModelHeuristicDetectsSlimFromTransparentOuterArmColumns()
    {
        var slim = PngValidator.Validate(CreatePng(64, 64, transparentArmOuterColumns: true));
        var classic = PngValidator.Validate(CreatePng(64, 64, transparentArmOuterColumns: false));
        var legacy = PngValidator.Validate(CreatePng(64, 32));

        Assert.Equal(SkinModel.Slim, SkinModelHeuristic.Infer(slim.Image).Model);
        Assert.Equal(SkinModel.Classic, SkinModelHeuristic.Infer(classic.Image).Model);
        Assert.Equal(SkinModel.Unknown, SkinModelHeuristic.Infer(legacy.Image).Model);
    }

    [Fact]
    public async Task WardrobeImportDeduplicatesAndDeletesOnlyUnreferencedItems()
    {
        var store = new WardrobeStore(Path.Combine(_root, "wardrobe"));
        var skinPath = Path.Combine(_root, "skin.png");
        await File.WriteAllBytesAsync(skinPath, CreatePng(64, 64, transparentArmOuterColumns: false));

        var first = await store.ImportAsync(skinPath, WardrobeTextureType.Skin, "我的皮肤");
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(first.Value!.Created);

        var second = await store.ImportAsync(skinPath, WardrobeTextureType.Skin, "重复");
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.False(second.Value!.Created);
        Assert.Equal(first.Value.Item.Id, second.Value.Item.Id);
        Assert.Single(store.LoadIndex().Index.Items);

        var refused = await store.DeleteAsync(first.Value.Item.Id, _ => ["instance:demo"]);
        Assert.Equal("wardrobe.item_referenced", refused.Error?.Code);
        Assert.NotNull(store.GetItem(first.Value.Item.Id));

        var deleted = await store.DeleteAsync(first.Value.Item.Id);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Null(store.GetItem(first.Value.Item.Id));
    }

    [Fact]
    public async Task WardrobeIndexCorruptionIsReportedButRecoverable()
    {
        var wardrobeRoot = Path.Combine(_root, "wardrobe");
        Directory.CreateDirectory(wardrobeRoot);
        await File.WriteAllTextAsync(Path.Combine(wardrobeRoot, "index.json"), "{ not json");
        var store = new WardrobeStore(wardrobeRoot);

        var load = store.LoadIndex();
        Assert.True(load.RecoveredFromCorruption);
        Assert.NotNull(load.Warning);

        var skinPath = Path.Combine(_root, "skin.png");
        await File.WriteAllBytesAsync(skinPath, CreatePng(64, 64, transparentArmOuterColumns: false));
        var imported = await store.ImportAsync(skinPath, WardrobeTextureType.Skin);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        Assert.Single(store.LoadIndex().Index.Items);
    }

    [Fact]
    public async Task OfflineAndMicrosoftProvidersDeclareOnlyVerifiedCapabilities()
    {
        var offline = new OfflineAppearanceProvider();
        var account = new Account
        {
            Id = "offline-1",
            Type = AccountType.Offline,
            Name = "Steve",
            Uuid = "00000000-0000-0000-0000-000000000000",
        };
        var offlineCapabilities = await offline.GetCapabilitiesAsync(account);
        Assert.True(offlineCapabilities.Supports(AppearanceCapability.UseLocalWardrobe));
        Assert.False(offlineCapabilities.Supports(AppearanceCapability.UploadSkin));

        var request = new AppearanceRequest { Operation = AppearanceOperation.UploadSkin, Account = account };
        Assert.Equal(AppearanceOperationStatus.Unsupported, (await offline.ExecuteAsync(request)).Status);

        var microsoftAccount = account with { Type = AccountType.Microsoft, Id = "ms-1" };
        var microsoft = new MicrosoftAppearanceProvider();
        var microsoftCapabilities = await microsoft.GetCapabilitiesAsync(microsoftAccount);
        Assert.Equal(AppearanceCapability.None, microsoftCapabilities.Capabilities);
        Assert.False(string.IsNullOrWhiteSpace(microsoftCapabilities.Reason));
        Assert.Equal(
            AppearanceOperationStatus.Unsupported,
            (await microsoft.ExecuteAsync(new AppearanceRequest
            {
                Operation = AppearanceOperation.UploadSkin,
                Account = microsoftAccount,
            })).Status);
    }

    [Fact]
    public void AuthlibCapabilitiesComeOnlyFromUploadableTextures()
    {
        var both = AuthlibTextureCapabilities.Parse(Profile("skin,cape"));
        Assert.True(both.IsSuccess);
        Assert.True(both.Value!.CanUploadSkin);
        Assert.True(both.Value.CanUploadCape);

        var skinOnly = AuthlibTextureCapabilities.Parse(Profile("skin", "skin"));
        Assert.True(skinOnly.Value!.CanUploadSkin);
        Assert.False(skinOnly.Value.CanUploadCape);

        var none = AuthlibTextureCapabilities.Parse("""{"properties":[]}""");
        Assert.True(none.IsSuccess);
        Assert.False(none.Value!.CanUploadSkin);
        Assert.False(none.Value.CanUploadCape);

        var invalid = AuthlibTextureCapabilities.Parse("{not json");
        Assert.False(invalid.IsSuccess);
    }

    [Fact]
    public async Task AuthlibUploadUsesBearerAndDistinguishesProfileUpdate()
    {
        var handler = new FakeAuthProfileHandler(before: Profile("before", "skin,cape"), after: Profile("after", "skin,cape"));
        var client = new AuthlibTextureUploadClient(new HttpClient(handler));
        var server = new AuthServer { Id = "test", Url = "https://auth.example.test", Name = "Test" };
        var png = CreatePng(64, 64, transparentArmOuterColumns: true);

        var result = await client.UploadAsync(
            server,
            "secret-token",
            "00000000-0000-0000-0000-000000000000",
            WardrobeTextureType.Skin,
            png,
            SkinModel.Slim);

        Assert.Equal(AppearanceOperationStatus.Succeeded, result.Status);
        Assert.True(result.RequestAccepted);
        Assert.True(result.ProfileUpdated);
        Assert.True(result.GameVerificationPending);
        Assert.DoesNotContain("secret-token", result.Message);
        Assert.DoesNotContain("secret-token", result.Detail ?? string.Empty);

        var upload = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Equal(
            "https://auth.example.test/api/user/profile/00000000000000000000000000000000/skin",
            upload.Uri.ToString());
        Assert.Equal("Bearer", upload.AuthorizationScheme);
        Assert.Equal("secret-token", upload.AuthorizationParameter);
        Assert.Contains("multipart/form-data", upload.ContentType);
        Assert.Contains("slim", upload.Body);
        Assert.Contains("skin.png", upload.Body);
    }

    [Fact]
    public async Task AuthlibRejectsInsecureServerRedirectAndUndeclaredCapability()
    {
        var png = CreatePng(64, 64, transparentArmOuterColumns: false);
        var insecure = new AuthlibTextureUploadClient(new HttpClient(new FakeAuthProfileHandler(Profile("a", "skin,cape"), Profile("a", "skin,cape"))));
        var insecureServer = new AuthServer { Id = "insecure", Url = "http://auth.example.test", Name = "Insecure" };
        var insecureResult = await insecure.UploadAsync(
            insecureServer,
            "token",
            "00000000-0000-0000-0000-000000000000",
            WardrobeTextureType.Skin,
            png);
        Assert.Equal(AppearanceOperationStatus.InvalidInput, insecureResult.Status);

        var redirectHandler = new FakeAuthProfileHandler(Profile("a", "skin,cape"), Profile("a", "skin,cape"))
        {
            UploadResponse = new HttpResponseMessage(HttpStatusCode.Redirect),
        };
        var redirect = new AuthlibTextureUploadClient(new HttpClient(redirectHandler));
        var redirectResult = await redirect.UploadAsync(
            new AuthServer { Id = "t", Url = "https://auth.example.test", Name = "T" },
            "token",
            "00000000-0000-0000-0000-000000000000",
            WardrobeTextureType.Skin,
            png);
        Assert.Equal(AppearanceOperationStatus.RedirectRejected, redirectResult.Status);
        Assert.DoesNotContain("token", redirectResult.Message);

        var capeHandler = new FakeAuthProfileHandler(Profile("a", "skin"), Profile("a", "skin"));
        var cape = new AuthlibTextureUploadClient(new HttpClient(capeHandler));
        var capeResult = await cape.UploadAsync(
            new AuthServer { Id = "t", Url = "https://auth.example.test", Name = "T" },
            "token",
            "00000000-0000-0000-0000-000000000000",
            WardrobeTextureType.Cape,
            png);
        Assert.Equal(AppearanceOperationStatus.Unsupported, capeResult.Status);
        Assert.DoesNotContain(capeHandler.Requests, request => request.Method == HttpMethod.Put);

        var invalidPng = await cape.UploadAsync(
            new AuthServer { Id = "t", Url = "https://auth.example.test", Name = "T" },
            "token",
            "00000000-0000-0000-0000-000000000000",
            WardrobeTextureType.Skin,
            [1, 2, 3]);
        Assert.Equal(AppearanceOperationStatus.InvalidInput, invalidPng.Status);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Profile(string textureValue, string uploadable = "skin,cape") =>
        JsonSerializer.Serialize(new
        {
            properties = new object[]
            {
                new { name = "uploadableTextures", value = uploadable },
                new { name = "textures", value = textureValue, signature = "" },
            },
        });

    private static byte[] CreatePng(int width, int height, bool transparentArmOuterColumns = false)
    {
        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            raw[row] = 0;
            for (var x = 0; x < width; x++)
            {
                var offset = row + 1 + (x * 4);
                raw[offset] = 120;
                raw[offset + 1] = 80;
                raw[offset + 2] = 40;
                raw[offset + 3] = 255;
            }
        }

        if (transparentArmOuterColumns)
        {
            foreach (var (startX, startY) in new[] { (54, 20), (46, 52) })
            {
                for (var y = startY; y < startY + 12; y++)
                {
                    for (var x = startX; x < startX + 2; x++)
                    {
                        raw[(y * (stride + 1)) + 1 + (x * 4) + 3] = 0;
                    }
                }
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        WriteInt32(header, 0, width);
        WriteInt32(header, 4, height);
        header[8] = 8;
        header[9] = (byte)PngColorType.Rgba;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt32(length, 0, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        WriteUInt32(crc, 0, ComputeCrc(crcInput));
        stream.Write(crc);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ComputeCrc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed class FakeAuthProfileHandler(string before, string after) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public HttpResponseMessage UploadResponse { get; init; } = new(HttpStatusCode.NoContent);

        private int _profileReads;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                body));

            if (request.Method == HttpMethod.Get)
            {
                var payload = _profileReads++ == 0 ? before : after;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                return response;
            }

            return UploadResponse;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string ContentType,
        string Body);
}
