using System;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Behavior
{
    public sealed class RecipientSecretDispatch
    {
        public int ActorId;
        public int RecipientId;
        public int SecretId;
        public string SecretText;
    }

    /// <summary>
    /// Strict reader for a frozen secret-disclosure dispatch. Both first dispatch and Saga
    /// recovery consume this shape, so neither can reinterpret a display index or switch
    /// recipients after the authoritative query snapshot was taken.
    /// </summary>
    public static class RecipientSecretDispatchEnvelope
    {
        public static bool TryRead(JObject envelope, int expectedActorId,
            int expectedRecipientId, out RecipientSecretDispatch dispatch)
        {
            dispatch = null;
            if (envelope == null) return false;
            int? actorId = StrictInt(envelope["_actorId"]);
            int? recipientId = StrictInt(envelope["_targetId"]);
            int? secretId = StrictInt(envelope["secret_id"]);
            string text = envelope["_secret_text"]?.Type == JTokenType.String
                ? envelope.Value<string>("_secret_text") : null;
            if (!actorId.HasValue || !recipientId.HasValue || !secretId.HasValue
                || actorId.Value <= 0 || recipientId.Value <= 0
                || actorId.Value == recipientId.Value || secretId.Value < 0
                || expectedActorId > 0 && actorId.Value != expectedActorId
                || expectedRecipientId > 0 && recipientId.Value != expectedRecipientId
                || text == null || text.Length > 256 * 1024)
                return false;
            dispatch = new RecipientSecretDispatch
            {
                ActorId = actorId.Value,
                RecipientId = recipientId.Value,
                SecretId = secretId.Value,
                SecretText = text,
            };
            return true;
        }

        public static bool MatchesFrozenArguments(JObject envelope, JObject arguments,
            int expectedActorId, int expectedRecipientId)
        {
            if (!TryRead(envelope, expectedActorId, expectedRecipientId,
                out RecipientSecretDispatch dispatch) || arguments == null)
                return false;
            int? argumentSecretId = StrictInt(arguments["secret_id"]);
            string argumentText = arguments["_secret_text"]?.Type == JTokenType.String
                ? arguments.Value<string>("_secret_text") : null;
            return argumentSecretId == dispatch.SecretId
                && argumentText != null
                && string.Equals(argumentText, dispatch.SecretText, StringComparison.Ordinal);
        }

        private static int? StrictInt(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer) return null;
            try { return token.Value<int>(); }
            catch { return null; }
        }
    }
}
