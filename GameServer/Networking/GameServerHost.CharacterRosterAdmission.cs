using System;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Runtime;
using Game.Shared.Characters;
using Game.Shared.Protocol;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    // Admission already has a reliable response going to the client. Reuse it for the
    // character-roster cache fingerprint instead of adding another request/message.
    // The authoritative roster is loaded through the existing CharacterList service path;
    // only a stale/missing client cache will request the full list after admission.
    private void SendResponse(
        ClientSession session,
        uint requestId,
        AdmissionAuthenticationResponseMessage response)
    {
        if (!response.success || response.rosterRevision != 0L)
        {
            SendResponse(session, requestId, (INetSerializable)response);
            return;
        }

        _ = SendAdmissionResponseWithRosterRevisionAsync(session, requestId, response);
    }

    private async Task SendAdmissionResponseWithRosterRevisionAsync(
        ClientSession session,
        uint requestId,
        AdmissionAuthenticationResponseMessage response)
    {
        long rosterRevision = 0L;
        try
        {
            var result = await _runtime.SessionService
                .GetCharacterListAsync(session.SessionHandle, CancellationToken.None)
                .ConfigureAwait(false);

            if (result != null && result.Success)
            {
                CharacterSummary[] source = result.Characters ?? Array.Empty<CharacterSummary>();
                int count = Math.Min(source.Length, CharacterListResponseMessage.MaxCharacters);
                var characters = new CharacterSessionCharacterSummary[count];
                for (int i = 0; i < count; ++i)
                {
                    CharacterSummary summary = source[i];
                    characters[i] = new CharacterSessionCharacterSummary(
                        summary.CharacterId.Value,
                        summary.Name,
                        summary.MapId);
                }

                rosterRevision = CharacterRosterStateRevision.Compute(characters);
            }
        }
        catch
        {
            // Revision validation is a cache optimization, not an authentication gate.
            // Zero tells the client to use the existing CharacterList reconciliation path.
            rosterRevision = 0L;
        }

        QueueMainThreadCompletion(() =>
        {
            if (!IsCurrent(session))
                return;

            response.rosterRevision = rosterRevision;
            SendResponse(session, requestId, (INetSerializable)response);
        });
    }
}
