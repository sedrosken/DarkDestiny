using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.Physics.Animation
{
    public class MotionTableManager
    {
        public PhysicsObj PhysicsObj;
        public MotionTable Table;
        public MotionState State;
        public uint AnimationCounter;
        public LinkedList<AnimNode> PendingAnimations;

        public MotionTableManager()
        {
            Init();
        }

        public MotionTableManager(uint mtableID)
        {
            Init();

            if (mtableID != 0)
                SetMotionTableID(mtableID);
        }

        public void AnimationDone(bool success)
        {
            var node = PendingAnimations.First;
            if (node != null)
            {
                AnimationCounter++;
                do
                {
                    var entry = node.Value;

                    if (entry.NumAnims > AnimationCounter)
                        break;

                    if ((entry.Motion & (uint)CommandMask.Action) != 0)
                        State.remove_action_head();

                    var motionID = entry.Motion;
                    PhysicsObj.MotionDone(motionID, success);
                    AnimationCounter -= entry.NumAnims;

                    if (PendingAnimations.First != null)
                        PendingAnimations.RemoveFirst();

                    if (PhysicsObj.WeenieObj != null)
                        PhysicsObj.WeenieObj.OnMotionDone(motionID, success);

                    node = PendingAnimations.First;
                }
                while (node != null);

                if (AnimationCounter != 0 && node == null)
                    AnimationCounter = 0;
            }
        }

        /// <summary>
        /// Pending entries only complete when the Sequence finishes link animations (AnimDoneHook -> AnimationDone).
        /// If the Sequence is already sitting on its cyclic animation, there are no link animations left to finish,
        /// so any entry still waiting on NumAnims can never complete and blocks every OnMotionDone queued behind it
        /// (e.g. a cast gesture). Call this AFTER a physics update, once that update's anim hooks have been processed.
        /// Returns the number of entries flushed.
        /// </summary>
        public int FlushStrandedAnimations(Sequence sequence)
        {
            if (PendingAnimations.Count == 0 || sequence == null || !sequence.is_first_cyclic())
                return 0;

            var waiting = false;
            uint totalAnims = 0;
            foreach (var entry in PendingAnimations)
            {
                if (entry.NumAnims > 0)
                    waiting = true;

                totalAnims += entry.NumAnims;
            }

            if (!waiting)
                return 0;

            var count = PendingAnimations.Count;

            // same drain HandleEnterWorld uses, but as completed motions.
            // bounded to the entries present now, in case an OnMotionDone handler queues new motions mid-drain
            var maxIterations = totalAnims + (uint)count + 1;
            for (var i = 0; i < maxIterations && PendingAnimations.Count > 0; i++)
                AnimationDone(true);

            return count;
        }

        public void CheckForCompletedMotions()
        {
            while (PendingAnimations.First != null)
            {
                var pendingAnimation = PendingAnimations.First;

                if (pendingAnimation.Value.NumAnims != 0)
                    return;

                var motionID = pendingAnimation.Value.Motion;

                if ((motionID & (uint)CommandMask.Action) != 0)
                    State.remove_action_head();

                PhysicsObj.MotionDone(motionID, true);

                if (PendingAnimations.First != null)
                    PendingAnimations.Remove(pendingAnimation);

                if (PhysicsObj.WeenieObj != null)
                    PhysicsObj.WeenieObj.OnMotionDone(motionID, true);
            }
        }

        public static MotionTableManager Create(uint mtableID)
        {
            return new MotionTableManager(mtableID);
        }

        public uint GetMotionTableID(uint mtableID)
        {
            return Table == null ? 0u : Table.ID;
        }

        public void Init()
        {
            State = new MotionState();
            PendingAnimations = new LinkedList<AnimNode>();
        }

        public void HandleEnterWorld(Sequence sequence)
        {
            sequence.remove_all_link_animations();
            while (PendingAnimations.Count > 0)
                AnimationDone(false);
        }

        public void HandleExitWorld()
        {
            while (PendingAnimations.Count > 0)
                AnimationDone(false);
        }

        public WeenieError PerformMovement(MovementStruct mvs, Sequence seq)
        {
            if (Table == null) return WeenieError.NoAnimationTable;

            uint counter = 0;
            switch (mvs.Type)
            {
                case MovementType.InterpretedCommand:
                    if (!Table.DoObjectMotion(mvs.Motion, State, seq, mvs.Params.Speed, ref counter))
                        return WeenieError.NoMtableData;

                    add_to_queue(mvs.Motion, counter, seq);
                    return WeenieError.None;

                case MovementType.StopInterpretedCommand:
                    if (!Table.StopObjectMotion(mvs.Motion, mvs.Params.Speed, State, seq, ref counter))
                        return WeenieError.NoMtableData;

                    add_to_queue((uint)MotionCommand.Ready, counter, seq);
                    return WeenieError.None;

                case MovementType.StopCompletely:
                    Table.StopObjectCompletely(State, seq, ref counter);
                    add_to_queue((uint)MotionCommand.Ready, counter, seq);
                    return WeenieError.None;

                default:
                    return WeenieError.None;    // ??
            }
        }

        public bool SetMotionTableID(uint mtableID)
        {
            Table = MotionTable.Get(mtableID);
            return Table != null;
        }

        public void SetPhysicsObject(PhysicsObj obj)
        {
            PhysicsObj = obj;
        }

        public void UseTime()
        {
            CheckForCompletedMotions();
        }

        public void add_to_queue(uint motion, uint num_anims, Sequence sequence)
        {
            PendingAnimations.AddLast(new AnimNode(motion, num_anims));
            remove_redundant_links(sequence);
        }

        public void initialize_state(Sequence sequence)
        {
            uint numAnims = 0;

            if (Table != null)
                Table.SetDefaultState(State, sequence, ref numAnims);

            add_to_queue((uint)MotionCommand.Ready, numAnims, sequence);
        }

        public void remove_redundant_links(Sequence sequence)
        {
            var node = PendingAnimations.Last;

            while (node != null)
            {
                var entry = node.Value;

                if (entry.NumAnims != 0)
                {
                    if ((entry.Motion & (uint)CommandMask.SubState) == 0 || (entry.Motion & (uint)CommandMask.Modifier) != 0)
                    {
                        if ((entry.Motion & (uint)CommandMask.Style) == 0)
                            return;

                        if (remove_redundant_links_inner(node, sequence, true))
                            return;
                    }
                    else
                    {
                        if (remove_redundant_links_inner(node, sequence, false))
                            return;
                    }
                }
                node = node.Previous;
            }
        }

        public bool remove_redundant_links_inner(LinkedListNode<AnimNode> node, Sequence sequence, bool first)
        {
            var entry = node.Value;
            var prev = node.Previous;

            var motion = first ? 0x70000000 : 0xB0000000;

            while (prev != null)
            {
                var prevEntry = prev.Value;

                if (entry.Motion == prevEntry.Motion && (first || prevEntry.NumAnims != 0))
                {
                    trancuate_animation_list(prev, sequence);
                    return true;
                }

                if (prevEntry.NumAnims != 0 && (prevEntry.Motion & motion) != 0)
                    return true;

                prev = prev.Previous;
            }

            return false;
        }

        public void trancuate_animation_list(LinkedListNode<AnimNode> node, Sequence sequence)
        {
            if (node == null) return;

            uint totalAnims = 0;
            var entry = PendingAnimations.Last;

            while (entry != node)
            {
                if (entry == null) return;

                totalAnims += entry.Value.NumAnims;
                entry.Value.NumAnims = 0;
                entry = entry.Previous;
            }
            sequence.remove_link_animations(totalAnims);
        }
    }
}
