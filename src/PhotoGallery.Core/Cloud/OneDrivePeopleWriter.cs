using System.Net;
using PhotoGallery.Core.Data;

namespace PhotoGallery.Core.Cloud;

public sealed record PeopleSendResult(int Sent, int Refused, int Waiting);

/// <summary>
/// Sends names and merges made in the gallery to OneDrive (<see cref="PeopleRepository.GetChanges"/>), in the order
/// they were made. It stops at the first change that can't be sent now (OneDrive busy or unreachable) so the order
/// holds, and tries again later. Changes OneDrive refuses are set aside for the user. Not connected to OneDrive:
/// <see cref="OneDriveWebUnavailableException"/>, and everything waits.
/// </summary>
public sealed class OneDrivePeopleWriter(OneDrivePeopleClient client, PeopleRepository people)
{
    private string? _driveId;

    public async Task<PeopleSendResult> SendAsync(CancellationToken ct = default)
    {
        var changes = people.GetChanges();
        if (changes.Count == 0) return new(0, 0, 0);
        _driveId ??= await client.GetDriveIdAsync(ct);
        int sent = 0, refused = 0;
        foreach (var change in changes)
        {
            try
            {
                switch (change.Kind)
                {
                    case PeopleChangeKind.Rename when change.Name is { } name:
                        await client.RenameAsync(_driveId, change.OneDrivePersonId, name, ct);
                        break;
                    case PeopleChangeKind.Merge when change.IntoPersonId is { } into && change.Name is { } name:
                        await client.MergeAsync(_driveId, change.OneDrivePersonId, into, name, ct);
                        break;
                    default:
                        people.FailChange(change.Id, $"Not a change OneDrive can take ({change.Kind}).", refused: true);
                        refused++;
                        continue;
                }
                people.CompleteChange(change);
                sent++;
                Log.Info($"People: sent a {change.Kind} to OneDrive");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && change.Kind == PeopleChangeKind.Merge)
            {
                // Merged already (in OneDrive, or by an earlier try whose answer got lost): nothing left to do.
                people.CompleteChange(change);
                sent++;
                Log.Info("People: a merge was already done in OneDrive");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict
                                                      or HttpStatusCode.Gone or HttpStatusCode.UnprocessableEntity)
            {
                people.FailChange(change.Id, ex.StatusCode == HttpStatusCode.NotFound ? "OneDrive no longer has this person." : ex.Message, refused: true);
                refused++;
                Log.Info($"People: OneDrive refused a {change.Kind} ({(int?)ex.StatusCode})");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                people.FailChange(change.Id, ex.Message, refused: false);
                Log.Info($"People: couldn't send a {change.Kind} to OneDrive yet ({ex.Message})");
                return new(sent, refused, changes.Count - sent - refused);
            }
        }
        return new(sent, refused, 0);
    }
}
