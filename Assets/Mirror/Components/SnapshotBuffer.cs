using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Mirror.TransformSyncing
{
    public struct TransformState
    {
        public readonly Vector3 position;
        public readonly Quaternion rotation;

        public TransformState(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public override string ToString()
        {
            return $"[{position}, {rotation}]";
        }
    }

    public static class TransformStateWriter
    {
        public static void WriteTransformState(this NetworkWriter writer, TransformState value)
        {
            writer.WriteVector3(value.position);
            writer.WriteUInt32(Compression.CompressQuaternion(value.rotation));
        }

        public static TransformState ReadTransformState(this NetworkReader reader)
        {
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = Compression.DecompressQuaternion(reader.ReadUInt32());

            return new TransformState(position, rotation);
        }
    }

    public class SnapshotBuffer
    {
        static readonly ILogger logger = LogFactory.GetLogger<SnapshotBuffer>(LogType.Error);

        struct Snapshot
        {
            /// <summary>
            /// Server Time
            /// </summary>
            public readonly double time;
            public readonly TransformState state;

            public Snapshot(TransformState state, double time) : this()
            {
                this.state = state;
                this.time = time;
            }
        }

        readonly List<Snapshot> buffer = new List<Snapshot>();

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => buffer.Count == 0;
        }

        public void AddSnapShot(TransformState state, double serverTime)
        {
            buffer.Add(new Snapshot(state, serverTime));
        }

        /// <summary>
        /// Gets snapshot to use for interpolation
        /// <para>this method should not be called when there are no snapshots in buffer</para>
        /// </summary>
        /// <param name="now"></param>
        /// <returns></returns>
        public TransformState GetLinearInterpolation(double now)
        {
            if (buffer.Count == 0)
            {
                logger.LogError("No snapshots, returning default");
                return default;
            }

            // first snapshot
            if (buffer.Count == 1)
            {
                if (logger.LogEnabled()) logger.Log("First snapshot");

                Snapshot only = buffer[0];
                return only.state;
            }

            // if first snapshot is after now, there is no "from", so return same as first snapshot
            Snapshot first = buffer[0];
            if (first.time > now)
            {
                if (logger.LogEnabled()) logger.Log($"No snapshots for t={now:0.000}, using earliest t={buffer[0].time:0.000}");

                return first.state;
            }

            // if last snapshot is before now, there is no "to", so return last snapshot
            // this can happen if server hasn't sent new data
            // there could be no new data from either lag or because object hasn't moved
            Snapshot last = buffer[buffer.Count - 1];
            if (last.time < now)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"No snapshots for t={now:0.000}, using first t={buffer[0].time:0.000} last t={last.time:0.000}");

                return last.state;
            }

            // edge cases are returned about, if code gets to this for loop then a valid from/to should exist
            for (int i = 0; i < buffer.Count - 1; i++)
            {
                Snapshot from = buffer[i];
                Snapshot to = buffer[i + 1];
                double fromTime = buffer[i].time;
                double toTime = buffer[i + 1].time;

                // if between times, then use from/to
                if (fromTime < now && now < toTime)
                {
                    float alpha = (float)Clamp01((now - fromTime) / (toTime - fromTime));
                    if (logger.LogEnabled()) { logger.Log($"alpha:{alpha}"); }
                    Vector3 pos = Vector3.Lerp(from.state.position, to.state.position, alpha);
                    Quaternion rot = Quaternion.Slerp(from.state.rotation, to.state.rotation, alpha);
                    return new TransformState(pos, rot);
                }
            }

            logger.LogError("Should never be here! Code should have return from if or for loop above.");
            return last.state;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double Clamp01(double v)
        {
            if (v < 0) { return 0; }
            if (v > 1) { return 1; }
            else { return v; }
        }

        /// <summary>
        /// removes snapshots older than <paramref name="oldTime"/>, but keeps atleast <paramref name="keepCount"/> snapshots in buffer that are older than oldTime
        /// <para>
        /// Keep atleast 1 snapshot older than old time so there is something to interoplate from
        /// </para>
        /// </summary>
        /// <param name="oldTime"></param>
        /// <param name="keepCount">minium number of snapshots to keep in buffer</param>
        public void RemoveOldSnapshots(float oldTime, int keepCount)
        {
            int kept = 0;
            // loop from newest to oldest
            for (int i = buffer.Count - 1; i >= 0; i--)
            {
                // older than oldTime
                if (buffer[i].time < oldTime)
                {
                    if (kept < keepCount)
                    {
                        kept++;
                    }
                    else
                    {
                        buffer.RemoveAt(i);
                    }
                }
            }
        }

        public override string ToString()
        {
            if (buffer.Count == 0) { return "Buffer Empty"; }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"count:{buffer.Count}, minTime:{buffer[0].time}, maxTime:{buffer[buffer.Count - 1].time}");
            for (int i = 0; i < buffer.Count; i++)
            {
                builder.AppendLine($"  {i}: {buffer[i].time}");
            }
            return builder.ToString();
        }
    }
}
