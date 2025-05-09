using System.Collections;
using System.Collections.Generic;
using PicaVoxel;
using UnityEngine;
using Unity.VisualScripting;

namespace PicaVoxel.VisualScripting
{
    [UnitTitle("On Save Chunk Changes")]
    [UnitShortTitle("On Save Chunk Changes")]
    [UnitCategory("Events\\PicaVoxel")]
    public class OnSaveChunkChanges : EventUnit<ChunkChangesEventArgs>
    {
        [DoNotSerialize]
        public ValueOutput result;

        protected override bool register => true;

        public override EventHook GetHook(GraphReference reference)
        {
            return new EventHook("OnSaveChunkChanges");
        }

        protected override void Definition()
        {
            base.Definition();

            result = ValueOutput<ChunkChangesEventArgs>("Changes");
        }

        protected override bool ShouldTrigger(Flow flow, ChunkChangesEventArgs data)
        {
            return true;
        }

        // Setting the value on our port.
        protected override void AssignArguments(Flow flow, ChunkChangesEventArgs data)
        {
            flow.SetValue(result, data);
        }
    }
}
