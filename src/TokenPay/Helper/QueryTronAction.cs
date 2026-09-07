using Flurl;
using Flurl.Http;
using HDWallet.Core;
using HDWallet.Tron;
using Microsoft.Extensions.Configuration;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.ABI.Model;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer.Crypto;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parameter = Nethereum.ABI.Model.Parameter;
using Org.BouncyCastle.Asn1.Ocsp;
using Signature = HDWallet.Core.Signature;
using TokenPay.Extensions;
using TokenPay.Models;
using TokenPay.Models.Transfer;
using HDWallet.Secp256k1;

namespace TokenPay.Helper
{

    public static partial class QueryTronAction
    {
        public static IConfiguration configuration { get; set; } = null!;
        /// <summary>
        /// 获取USDT余额
        /// </summary>
        /// <param name="Address"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<decimal> GetUsdtAmountAsync(string Address, CancellationToken cancellationToken = default)
        {
            var hex = Address.Base58ToHex();
            var encoded = new FunctionCallEncoder().EncodeParameters(new Parameter[] {
                new Parameter("address","who")
                }, new string[] { "0x" + hex.Substring(2, hex.Length - 2) });

            var encodedHex = Convert.ToHexString(encoded);
            var BaseUrl = configuration.GetValue("TronApiHost", "https://api.trongrid.io");
            var request = BaseUrl
                .AppendPathSegment("wallet/triggerconstantcontract")
                .WithTimeout(5);
            var apiKey = configuration.GetValue<string>("TRON-PRO-API-KEY");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request = request.WithHeader("TRON-PRO-API-KEY", apiKey);
            }

            var ContractAddress = configuration.GetValue("ContractAddress", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t");
            var result = await request.PostJsonAsync(new
            {
                owner_address = Address,
                contract_address = ContractAddress,
                function_selector = "balanceOf(address)",
                parameter = encodedHex,
                visible = true
            }, cancellationToken: cancellationToken).ReceiveJson<BalanceOfModel>();

            if (result.Result.Result)
            {
                Log.Logger.Information("检查余额：{address}", Address);
                //Log.Logger.Information("金额：{@result}", result);
                var amountAbi = result.ConstantResult.FirstOrDefault();
                if (!string.IsNullOrEmpty(amountAbi))
                {
                    amountAbi = amountAbi.TrimStart('0');
                    if (long.TryParse(amountAbi, NumberStyles.HexNumber, null, out var amount))
                    {
                        return amount / 1_000_000m;
                    }
                }
            }
            return 0;
        }
        /// <summary>
        /// 获取TRX余额
        /// </summary>
        /// <param name="address"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<decimal> GetTRXAsync(string address, CancellationToken cancellationToken = default)
        {
            var BaseUrl = configuration.GetValue("TronApiHost", "https://api.trongrid.io");
            var request = BaseUrl
                .AppendPathSegment("wallet/getaccount")
                .WithTimeout(5);
            var apiKey = configuration.GetValue<string>("TRON-PRO-API-KEY");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request = request.WithHeader("TRON-PRO-API-KEY", apiKey);
            }

            var result = await request.PostJsonAsync(new
            {
                address,
                visible = true
            }, cancellationToken: cancellationToken).ReceiveJson<GetAccountModel>();
            return (result?.Balance ?? 0) / 1_000_000m;
        }
    }
}
