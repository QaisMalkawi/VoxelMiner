using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VoxelMiner.Core;
using VoxelMiner.Gameplay;

namespace VoxelMiner.Entities
{
    public interface IDamagableEntity
    {
        public float MaxHealth { get; init; }
        public float Health { get; set; }
        public ItemStack DropItems { get; set; } 

        public abstract bool Damage(float Damage);
    }
}
