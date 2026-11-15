using System;
using NUnit.Framework;
using Wirenook.WorldSync.Core;

namespace Wirenook.WorldSync.Tests
{
    public class WorldGridTests
    {
        private static readonly float[] Ranges = { WorldGrid.MinimumRange, 512f, 1000f, 4000f };

        /// <summary>The whole path a coordinate takes, from the wearer's rig to a remote client.</summary>
        private static float RoundTrip(WorldGrid grid, float metres, bool truncate)
        {
            // The wearer's client: both readings come off blended constraints.
            var coarsePoint = WorldGrid.Blend(0f, grid.CoarseOriginWeight, metres);
            var index = grid.IndexFromReading(coarsePoint, truncate);
            var finePoint = WorldGrid.Blend(grid.CellOrigin(index), grid.FineOriginWeight, metres) - grid.CellOrigin(index);
            // The network, then a remote client.
            return grid.Decode(index, WorldGrid.Synced(finePoint));
        }

        [Test]
        public void ReconstructsEveryPointWithinTheStatedResolution()
        {
            foreach (var range in Ranges)
            {
                var grid = new WorldGrid(range);
                foreach (var truncate in new[] { false, true })
                {
                    var worst = 0f;
                    for (var i = 0; i <= 20000; i++)
                    {
                        var metres = -range + 2f * range * i / 20000f;
                        worst = Math.Max(worst, Math.Abs(RoundTrip(grid, metres, truncate) - metres));
                    }

                    // Slack for single-precision arithmetic: positions kilometres
                    // from the origin are themselves only good to a fraction of a millimetre,
                    // and the blends divide by weights in the thousands.
                    Assert.LessOrEqual(worst, grid.Resolution * 1.02f + range * 4e-6f,
                        "range " + range + (truncate ? " truncating" : " rounding"));
                }
            }
        }

        [Test]
        public void ResolutionIsCentimetresAtUsefulRanges()
        {
            Assert.Less(new WorldGrid(512f).Resolution, 0.02f);
            Assert.Less(new WorldGrid(1000f).Resolution, 0.035f);
        }

        [Test]
        public void TheOriginSitsAtTheMiddleIndex()
        {
            var grid = new WorldGrid(512f);
            Assert.AreEqual(0f, grid.CellOrigin(WorldGrid.OriginIndex), 1e-6f);
            Assert.AreEqual(WorldGrid.OriginIndex, grid.IndexFromReading(grid.CoarseReading(0f), false));
            Assert.AreEqual(WorldGrid.OriginIndex, grid.IndexFromReading(grid.CoarseReading(0f), true));
        }

        [Test]
        public void TheEndsOfTheRangeUseTheEndsOfTheIndex()
        {
            var grid = new WorldGrid(512f);
            Assert.AreEqual(0, grid.IndexFromReading(grid.CoarseReading(-512f), true));
            Assert.AreEqual(WorldGrid.MaxIndex, grid.IndexFromReading(grid.CoarseReading(512f), false));
            Assert.AreEqual(-512f, grid.CellOrigin(0), 1e-3f);
            Assert.AreEqual(512f, grid.CellOrigin(WorldGrid.MaxIndex), 1e-3f);
        }

        [Test]
        public void PointsBeyondTheRangeClampToItsEdge()
        {
            var grid = new WorldGrid(512f);
            Assert.AreEqual(WorldGrid.MaxIndex, grid.IndexFromReading(grid.CoarseReading(900f), false));
            Assert.AreEqual(0, grid.IndexFromReading(grid.CoarseReading(-900f), false));
        }

        [Test]
        public void ReadingsStayWithinAContactsReach()
        {
            foreach (var range in Ranges)
            {
                var grid = new WorldGrid(range);
                for (var i = 0; i <= 2000; i++)
                {
                    var metres = -range + 2f * range * i / 2000f;
                    var coarse = WorldGrid.Blend(0f, grid.CoarseOriginWeight, metres);
                    Assert.LessOrEqual(Math.Abs(coarse), WorldGrid.CoarseReach * 1.0001f);
                    foreach (var truncate in new[] { false, true })
                    {
                        var index = grid.IndexFromReading(coarse, truncate);
                        var fine = WorldGrid.Blend(grid.CellOrigin(index), grid.FineOriginWeight, metres) - grid.CellOrigin(index);
                        Assert.LessOrEqual(Math.Abs(fine), WorldGrid.FineReach * 1.0001f);
                        Assert.AreEqual(grid.Fine(metres, index), fine, 1e-3f);
                    }
                }
            }
        }

        [Test]
        public void TheCoarseReadingIsTheCoordinateScaledDown()
        {
            var grid = new WorldGrid(1000f);
            Assert.AreEqual(grid.CoarseReading(730f), WorldGrid.Blend(0f, grid.CoarseOriginWeight, 730f), 1e-5f);
            Assert.AreEqual(1.46f, grid.CoarseReading(730f), 1e-5f);
        }

        [Test]
        public void BlendWeightsArePositive()
        {
            foreach (var range in Ranges)
            {
                var grid = new WorldGrid(range);
                Assert.Greater(grid.CoarseOriginWeight, 0f);
                Assert.Greater(grid.FineOriginWeight, 0f);
            }
        }

        [Test]
        public void RejectsARangeTooSmallToMeasure()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldGrid(100f));
        }

        [Test]
        public void SyncedValuesLandOnTheParametersSteps()
        {
            Assert.AreEqual(0f, WorldGrid.Synced(0.001f), 1e-6f);
            Assert.AreEqual(1f, WorldGrid.Synced(1.4f), 1e-6f);
            Assert.AreEqual(-1f, WorldGrid.Synced(-3f), 1e-6f);
            Assert.AreEqual(2f / WorldGrid.FloatSteps, WorldGrid.Synced(0.008f), 1e-6f);
        }
    }
}
