using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AElf.Management.Request
{
    public class HttpRequestHelper
    {
        private static async Task<string> DoGetRequest(string url = "")
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");
            using (var client = new HttpClient())
            {
                var response = await client.SendAsync(request);
                var result = await response.Content.ReadAsStringAsync();
                return result;
            }
        }

        public static async Task<T> Get<T>(string url)
        {
            var result = await DoGetRequest(url);
            return JsonConvert.DeserializeObject<T>(result);
        }
    }
}