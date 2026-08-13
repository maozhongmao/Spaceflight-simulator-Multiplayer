namespace SfsMultiplayer.Protocol;

public sealed record AuthorityParticipant(int PlayerId, int ControlledRocketId);

public static class AuthorityAllocationPolicy
{
    public static Dictionary<int, int> Allocate(
        IEnumerable<int> rocketIds,
        IEnumerable<AuthorityParticipant> participants)
    {
        var orderedParticipants = participants
            .OrderBy(participant => participant.PlayerId)
            .ToArray();
        var assignments = new Dictionary<int, int>();
        if (orderedParticipants.Length == 0)
            return assignments;

        var roundRobin = 0;
        foreach (var rocketId in rocketIds.OrderBy(id => id))
        {
            var owner = orderedParticipants.FirstOrDefault(
                participant => participant.ControlledRocketId == rocketId)
                ?? orderedParticipants[roundRobin++ % orderedParticipants.Length];
            assignments[rocketId] = owner.PlayerId;
        }
        return assignments;
    }
}
