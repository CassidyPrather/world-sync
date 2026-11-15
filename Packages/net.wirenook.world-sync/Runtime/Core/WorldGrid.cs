using System;

namespace Wirenook.WorldSync.Core
{
    /// <summary>
    /// How one world coordinate travels through VRChat's synced parameters.
    ///
    /// A synced parameter carries eight bits at most, which over a whole
    /// world is metres of error. So each axis is sent as two values: the
    /// index of a coarse cell, as an integer, and how far into the cell the
    /// point lies, as a float. The index is exact on every client; only the
    /// remainder is rounded by the network, and it only spans one cell.
    ///
    /// The numbers here are also what sizes the measuring rig: the wearer's
    /// client reads both values off contact receivers, and a contact only
    /// reaches a few metres, so world-sized distances are first scaled down
    /// by constraints that blend two sources. The blend weights that do that
    /// scaling come from this class.
    /// </summary>
    public sealed class WorldGrid
    {
        /// <summary>The largest cell index. Indices run from 0 to this, with the world origin in the middle.</summary>
        public const int MaxIndex = 254;

        /// <summary>The index of the cell that starts at the world origin.</summary>
        public const int OriginIndex = MaxIndex / 2;

        /// <summary>
        /// Distinct steps an 8-bit synced float has between -1 and 1. VRChat
        /// reserves two of the 256 codes, per the SDK's own precision readout.
        /// </summary>
        public const int FloatSteps = 254;

        /// <summary>
        /// How far from a receiver, in metres per axis, the coarse reading is
        /// allowed to land. A contact's radius is capped at three metres, and
        /// the reading can be this far out on all three axes at once, so the
        /// corner of this cube has to stay inside the two spheres' reach.
        /// </summary>
        public const float CoarseReach = 2f;

        /// <summary>The same for the fine reading, which is also the value synced, so it spans -1 to 1.</summary>
        public const float FineReach = 1f;

        /// <summary>Metres from the world origin, on each axis, that can be represented.</summary>
        public float Range { get; }

        public WorldGrid(float range)
        {
            // Below this a cell is under a metre wide and the fine reading's
            // blend weight would go negative.
            if (range < MinimumRange)
                throw new ArgumentOutOfRangeException(nameof(range), "The range must be at least " + MinimumRange + " m.");
            Range = range;
        }

        /// <summary>The smallest range a grid can cover; smaller worlds lose nothing by using it.</summary>
        public const float MinimumRange = 160f;

        /// <summary>Width of one coarse cell, in metres.</summary>
        public float Cell => 2f * Range / MaxIndex;

        /// <summary>
        /// The largest error a remote viewer can see on one axis, in metres:
        /// half a step of the fine value. The fine value spans a cell in each
        /// direction, because the index may have been rounded or truncated.
        /// </summary>
        public float Resolution => Cell / FloatSteps;

        /// <summary>World coordinate at which cell <paramref name="index"/> starts.</summary>
        public float CellOrigin(int index) => (index - OriginIndex) * Cell;

        /// <summary>
        /// What the coarse receiver reads for a point at
        /// <paramref name="metres"/>: the coordinate scaled into its reach.
        /// </summary>
        public float CoarseReading(float metres) => metres * CoarseReach / Range;

        /// <summary>
        /// Turns a coarse reading into a cell index, as the parameter driver
        /// does. VRChat does not document whether copying a float into an
        /// integer parameter rounds or truncates, so either may be asked for;
        /// the scheme is correct for both.
        /// </summary>
        public int IndexFromReading(float reading, bool truncate)
        {
            var scaled = (reading + CoarseReach) / (2f * CoarseReach) * MaxIndex;
            var index = truncate ? (int)Math.Floor(scaled) : (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            return Math.Max(0, Math.Min(MaxIndex, index));
        }

        /// <summary>
        /// The fine value for a point, given the cell it was assigned to: its
        /// distance from the cell's origin in cells. Stays within -1 to 1 for
        /// any point inside the range, rounded or truncated.
        /// </summary>
        public float Fine(float metres, int index) => (metres - CellOrigin(index)) / Cell * FineReach;

        /// <summary>The fine value as a remote client receives it: rounded to the nearest of its steps.</summary>
        public static float Synced(float fine)
        {
            var half = FloatSteps / 2f;
            var clamped = Math.Max(-1f, Math.Min(1f, fine));
            return (float)Math.Round(clamped * half, MidpointRounding.AwayFromZero) / half;
        }

        /// <summary>The coordinate a remote client reconstructs.</summary>
        public float Decode(int index, float fine) => CellOrigin(index) + fine / FineReach * Cell;

        /// <summary>
        /// Blend weight of the world origin in the constraint that produces
        /// the coarse reading, against a weight of 1 on the anchor. A point
        /// blended this way sits at the anchor's position scaled by
        /// <see cref="CoarseReach"/> over <see cref="Range"/>.
        /// </summary>
        public float CoarseOriginWeight => Range / CoarseReach - 1f;

        /// <summary>
        /// Blend weight of the cell origin in the constraint that produces the
        /// fine reading, against a weight of 1 on the anchor: the point sits
        /// at the anchor's distance from the cell origin, in cells.
        /// </summary>
        public float FineOriginWeight => Cell / FineReach - 1f;

        /// <summary>Where a constraint blending <paramref name="origin"/> and <paramref name="anchor"/> puts its object.</summary>
        public static float Blend(float origin, float originWeight, float anchor)
        {
            return (origin * originWeight + anchor) / (originWeight + 1f);
        }
    }
}
