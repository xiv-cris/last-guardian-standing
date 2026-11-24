using System;
using System.Linq;

namespace Quadra.Sim;

public enum ControllerKind { Empty, Human, Bot }
public enum TeamMode { FreeForAll, TwoVsTwo, OneOneTwo, OneVsThree }
public enum TeamLayout { Random, Adjacent, Opposite }
public enum LivesMode { Personal, TeamPool }
public enum BallMode { Single, Chaos }

public sealed class MatchConfig
{
    /// <summary>Who controls each side, indexed by <see cref="GoalSide"/>.</summary>
    public ControllerKind[] Slots { get; private set; } =
    {
        ControllerKind.Bot, ControllerKind.Bot, ControllerKind.Human, ControllerKind.Bot,
    };

    public TeamMode TeamMode { get; set; } = TeamMode.FreeForAll;
    public TeamLayout Layout { get; set; } = TeamLayout.Random;
    public LivesMode LivesMode { get; set; } = LivesMode.Personal;
    public BallMode BallMode { get; set; } = BallMode.Single;
    public int Lives { get; set; } = 5;
    public int RoundsToWin { get; set; } = 3;
    public int MaxBalls { get; set; } = 10;
    public int? Seed { get; set; }

    public MatchConfig Clone()
    {
        var c = (MatchConfig)MemberwiseClone();
        c.Slots = (ControllerKind[])Slots.Clone();
        return c;
    }
}

public static class TeamAssigner
{
    /// <summary>Returns the team id of each side (indexed by <see cref="GoalSide"/>).</summary>
    public static int[] Assign(TeamMode mode, TeamLayout layout, Random rng, out TeamLayout resolved)
    {
        resolved = layout == TeamLayout.Random
            ? (rng.Next(2) == 0 ? TeamLayout.Adjacent : TeamLayout.Opposite)
            : layout;

        switch (mode)
        {
            case TeamMode.TwoVsTwo:
            {
                var (a, b) = PickPair(resolved, rng);
                return Enumerable.Range(0, 4).Select(i => i == a || i == b ? 0 : 1).ToArray();
            }
            case TeamMode.OneOneTwo:
            {
                var (a, b) = PickPair(resolved, rng);
                var teams = new int[4];
                int next = 1;
                for (int i = 0; i < 4; i++)
                    teams[i] = i == a || i == b ? 0 : next++;
                return teams;
            }
            case TeamMode.OneVsThree:
            {
                int solo = rng.Next(4);
                return Enumerable.Range(0, 4).Select(i => i == solo ? 0 : 1).ToArray();
            }
            default:
                return new[] { 0, 1, 2, 3 };
        }
    }

    static (int, int) PickPair(TeamLayout layout, Random rng)
    {
        if (layout == TeamLayout.Opposite)
        {
            int r = rng.Next(2);
            return (r, r + 2);
        }
        int s = rng.Next(4);
        return (s, (s + 1) % 4);
    }
}
