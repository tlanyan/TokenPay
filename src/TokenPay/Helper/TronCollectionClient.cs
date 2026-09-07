using Flurl.Http;
using Flurl.Http.Newtonsoft;
using HDWallet.Tron;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Numerics;
using TokenPay.Extensions;
using TokenPay.Models;

namespace TokenPay.Helper;

/// <summary>归集专用链上操作：资源查询、签名及广播；广播成功即返回。</summary>
internal sealed class TronCollectionClient : IDisposable
{
    private readonly FlurlClient client;
    private readonly string contract;

    public TronCollectionClient(IConfiguration configuration)
    {
        client = new FlurlClient(configuration.GetValue("TronApiHost", "https://api.trongrid.io"));
        client.WithSettings(s =>
        {
            s.JsonSerializer = new NewtonsoftJsonSerializer();
            s.Timeout = TimeSpan.FromSeconds(15);
        });
        var apiKey = configuration.GetValue<string>("TRON-PRO-API-KEY");
        if (!string.IsNullOrWhiteSpace(apiKey)) client.WithHeader("TRON-PRO-API-KEY", apiKey);
        contract = configuration.GetValue("ContractAddress", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t")!;
    }

    public async Task<JObject> PostAsync(string path, object body, CancellationToken ct)
        => await client.Request(path).PostJsonAsync(body, cancellationToken: ct).ReceiveJson<JObject>();

    public async Task<Dictionary<string, long>> GetChainParametersAsync(CancellationToken ct)
    {
        var data = await client.Request("wallet/getchainparameters").GetJsonAsync<JObject>(cancellationToken: ct);
        return (data["chainParameter"] ?? throw new InvalidOperationException("无法读取链上手续费"))
            .ToDictionary(x => x.Value<string>("key")!, x => x.Value<long?>("value") ?? 0);
    }

    public async Task<JObject> AccountAsync(string address, CancellationToken ct)
        => await PostAsync("wallet/getaccount", new { address, visible = true }, ct);

    public async Task<decimal> TrxAsync(string address, CancellationToken ct)
        => (await AccountAsync(address, ct)).Value<long>("balance") / 1_000_000m;

    public async Task<GetAccountResourceModel> ResourcesAsync(string address, CancellationToken ct)
        => (await PostAsync("wallet/getaccountresource", new { address, visible = true }, ct))
            .ToObject<GetAccountResourceModel>() ?? throw new InvalidOperationException("资源查询失败");

    public async Task<decimal> UsdtAsync(string address, CancellationToken ct)
    {
        var result = await PostAsync("wallet/triggerconstantcontract", new
        {
            owner_address = address, contract_address = contract, visible = true,
            function_selector = "balanceOf(address)", parameter = AddressParameter(address)
        }, ct);
        RequireContractSuccess(result);
        var hex = result["constant_result"]?.First?.Value<string>();
        if (string.IsNullOrEmpty(hex)) throw new InvalidOperationException("USDT 余额响应为空");
        return (decimal)BigInteger.Parse("0" + hex, NumberStyles.HexNumber) / 1_000_000m;
    }

    public async Task<long> EstimateEnergyAsync(string from, string to, decimal amount, CancellationToken ct)
    {
        var result = await PostAsync("wallet/triggerconstantcontract", TransferBody(from, to, amount, 1_000_000_000), ct);
        RequireContractSuccess(result);
        var energy = result.Value<long>("energy_used");
        return energy > 0 ? energy : throw new InvalidOperationException("无法预估 USDT 能量消耗");
    }

    public async Task<JObject> BuildTrxAsync(TronWallet wallet, string to, decimal amount, CancellationToken ct)
        => Sign(wallet, await PostAsync("wallet/createtransaction", new
        {
            owner_address = wallet.Address, to_address = to, amount = Sun(amount), visible = true
        }, ct));

    public async Task<JObject> BuildUsdtAsync(TronWallet wallet, string to, decimal amount, long feeLimit, CancellationToken ct)
    {
        var result = await PostAsync("wallet/triggersmartcontract", TransferBody(wallet.Address, to, amount, feeLimit), ct);
        RequireContractSuccess(result);
        return Sign(wallet, (JObject?)result["transaction"] ?? throw new InvalidOperationException("缺少 USDT 交易"));
    }

    public async Task BroadcastAsync(JObject transaction, CancellationToken ct)
    {
        var response = await PostAsync("wallet/broadcasttransaction", transaction, ct);
        if (response.Value<bool>("result")) return;
        throw new InvalidOperationException($"交易广播失败：{response.Value<string>("code")} {response.Value<string>("message")}");
    }

    public static long Bandwidth(JObject transaction)
        => checked((transaction.Value<string>("raw_data_hex") ?? throw new InvalidOperationException("缺少 raw_data_hex")).Length / 2 + 3 + 67 + 64) + 3;

    public static bool HasBandwidth(GetAccountResourceModel resource, long required)
        => Math.Max(0, resource.NetLimit - resource.NetUsed) >= required ||
           Math.Max(0, resource.FreeNetLimit - resource.FreeNetUsed) >= required;

    public static long Sun(decimal amount)
    {
        if (amount <= 0 || amount * 1_000_000m != decimal.Truncate(amount * 1_000_000m))
            throw new InvalidOperationException("交易金额必须为正数且最多六位小数");
        return checked((long)(amount * 1_000_000m));
    }

    private static string AddressParameter(string address) => address.Base58ToHex()[2..].PadLeft(64, '0');

    private object TransferBody(string from, string to, decimal amount, long feeLimit) => new
    {
        owner_address = from, contract_address = contract, visible = true,
        function_selector = "transfer(address,uint256)",
        parameter = AddressParameter(to) + Sun(amount).ToString("x64"),
        fee_limit = feeLimit
    };

    private static void RequireContractSuccess(JObject result)
    {
        if (result["result"]?.Value<bool>("result") != true)
            throw new InvalidOperationException($"合约请求失败：{result["result"]?["message"]}");
    }

    private static JObject Sign(TronWallet wallet, JObject transaction)
    {
        var txid = transaction.Value<string>("txID");
        if (string.IsNullOrEmpty(txid) || transaction["raw_data_hex"] == null || transaction["raw_data"] is not JObject)
            throw new InvalidOperationException($"构建交易失败：{transaction.Value<string>("Error")}");
        var signature = new TronSignature(wallet.Sign(Convert.FromHexString(txid)));
        transaction["signature"] = new JArray(Convert.ToHexString(signature.SignatureBytes));
        return transaction;
    }

    public void Dispose() => client.Dispose();
}
