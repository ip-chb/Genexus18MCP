using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Common
{
    internal static class JsonIngress
    {
        internal static JObject ParseObject(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            using (var textReader = new StringReader(json))
            using (var jsonReader = new JsonTextReader(textReader)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double
            })
            {
                return JObject.Load(jsonReader);
            }
        }

        internal static JToken ParseToken(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            using (var textReader = new StringReader(json))
            using (var jsonReader = new JsonTextReader(textReader)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double
            })
            {
                return JToken.Load(jsonReader);
            }
        }
    }
}
