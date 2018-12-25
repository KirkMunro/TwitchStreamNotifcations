using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TwitchLib.Webhook.Models;
using Newtonsoft.Json;
using Stream = TwitchLib.Webhook.Models.Stream;

namespace Markekraus.TwitchStreamNotifications
{

    [StorageAccount("TwitchStreamStorage")]
    public static class TwitchWebhookIngestion
    {
        private const string SignatureHeader = "X-Hub-Signature";
        private static readonly string HashSecret = Environment.GetEnvironmentVariable("TwitchSubscriptionsHashSecret");

        [FunctionName("TwitchWebhookIngestion")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "TwitchWebhookIngestion/{StreamName}")] HttpRequest Req,
            string StreamName,
            [Queue("TwitchStreamActivity")] ICollector<Stream> queue,
            ILogger Log)
        {
            Log.LogInformation($"TwitchWebhookIngestion function processed a request. StreamName: {StreamName}");
            Log.LogInformation("Processing body stream");
            Log.LogInformation($"CanSeek: {Req.Body.CanSeek}");

            var bodyString = await Req.ReadAsStringAsync();

            StreamData webhook;
            try
            {
                webhook = JsonConvert.DeserializeObject<StreamData>(bodyString);
            }
            catch (Exception e)
            {
                Log.LogError($"Invalid JSON. exception {e.Message}. {bodyString}");
                return new BadRequestResult();
            }

            Log.LogInformation($"Request contains {webhook.Data.Count} objects.");

            if(!Req.Headers.TryGetValue(SignatureHeader, out var signature))
            {
                Log.LogError($"Missing {SignatureHeader} header");
                return new BadRequestResult();
            }

            var fields = signature.ToString().Split("=");
            if (fields.Length != 2)
            {
                Log.LogError($"Malformed {SignatureHeader} header. Missing '='?");
                return new BadRequestObjectResult(signature);
            }

            var header = fields[1];
            if (string.IsNullOrEmpty(header))
            {
                Log.LogError($"Malformed {SignatureHeader} header. Signature is null or empty");
                return new BadRequestObjectResult(fields);
            }

            var expectedHash = Utility.FromHex(header);
            if (expectedHash == null)
            {
                Log.LogError($"Malformed {SignatureHeader} header. Invalid hex signature");
                return new BadRequestObjectResult(SignatureHeader);
            }

            var actualHash = await Utility.ComputeRequestBodySha256HashAsync(Req, HashSecret);

            if(!Utility.SecretEqual(expectedHash, actualHash))
            {
                Log.LogError("Signature mismatch. actaulHash did not match expectedHash");
                return new BadRequestObjectResult(signature);
            }

            foreach (var item in webhook.Data)
            {
                Log.LogInformation($"Queing notification for stream {item.UserName} type {item.Type} started at {item.StartedAt}");
                queue.Add(item);
            }

            Log.LogInformation("Processing complete");
            return new OkResult();
        }
    }
}
