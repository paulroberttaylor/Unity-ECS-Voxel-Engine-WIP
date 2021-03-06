﻿using Unity.Entities;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;

//[DisableAutoCreation]
public class ChunkMeshGenerationSystem : JobComponentSystem
{
    EntityQuery query;

    DelayedChunkMeshTaggingBufferSystem system;

    protected override void OnCreate()
    {
        query = GetEntityQuery(new EntityQueryDesc()
        {
            All = new ComponentType[]
            {
                ComponentType.ReadOnly<Chunk>(),
                ComponentType.ReadOnly<BlockIDBuffer>(),
                ComponentType.ReadWrite<Triangle>(),
                ComponentType.ReadWrite<Normal>(),
                ComponentType.ReadWrite<Vertex>(),
                ComponentType.ReadWrite<Uv>()
            },
            Any = System.Array.Empty<ComponentType>(),
            //Chunk must be generated to have its mesh updated
            None = new ComponentType[]
            {
                ComponentType.ReadOnly<MeshDirty>(),
                ComponentType.ReadOnly<Ungenerated>(),
                ComponentType.ReadOnly<ChunkUpToDate>()
            }
        });


        //This shit doesn't work at all actually
        //query.SetFilterChanged(ComponentType.ReadOnly<BlockIDBuffer>());
        system = World.Active.GetOrCreateSystem<DelayedChunkMeshTaggingBufferSystem>();
    }

    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var job = new GenerateMeshJob
        {
            Vertices = GetBufferFromEntity<Vertex>(false),
            Normals = GetBufferFromEntity<Normal>(false),
            Tris = GetBufferFromEntity<Triangle>(false),
            Uvs = GetBufferFromEntity<Uv>(false),
            blocks = GetBufferFromEntity<BlockIDBuffer>(true),
            CommandBuffer = system.CreateCommandBuffer().ToConcurrent(),
            EntityType = GetArchetypeChunkEntityType(),
            meshesThisFrame = 0,
            ChunkEntities = Main.chunkEntities,
            chunk = GetComponentDataFromEntity<Chunk>(true)
        };
        inputDeps = job.Schedule<GenerateMeshJob>(query, inputDeps);
        system.AddJobHandleForProducer(inputDeps);
        return inputDeps;
    }

    protected override void OnDestroy()
    {
    }

    public class DelayedChunkMeshTaggingBufferSystem : EntityCommandBufferSystem { }
    //[BurstCompile]
    unsafe struct GenerateMeshJob : IJobChunk
    {
        //https://forum.unity.com/threads/is-it-better-to-use-ijobparallelfor-for-larger-or-smaller-processes.576856/#post-3842758
        //https://forum.unity.com/threads/nativearray-and-mesh.522951/#post-3839548x    
        [NativeDisableParallelForRestriction] public BufferFromEntity<Vertex> Vertices;
        [NativeDisableParallelForRestriction] public BufferFromEntity<Triangle> Tris;
        [NativeDisableParallelForRestriction] public BufferFromEntity<Uv> Uvs;
        [NativeDisableParallelForRestriction] public BufferFromEntity<Normal> Normals;

        [ReadOnly] public ArchetypeChunkEntityType EntityType;
        [ReadOnly] public ComponentDataFromEntity<Chunk> chunk;

        [NativeDisableParallelForRestriction] [ReadOnly] public BufferFromEntity<BlockIDBuffer> blocks;

        [NativeDisableParallelForRestriction] [ReadOnly] public NativeHashMap<int3, Entity> ChunkEntities;

        public EntityCommandBuffer.Concurrent CommandBuffer;
        public int meshesThisFrame;

        //TODO
        //Try https://forum.unity.com/threads/dynamicbuffer-is-awfully-slow.728663/
        //instead of add-ing
        public void Execute(ArchetypeChunk archChunk, int chunkIndex, int firstEntityIndex)
        {
            //Testing updating meshes slowly
            if(meshesThisFrame > 8)
                return;

            meshesThisFrame++;
            const ushort transparent = 1 << 15;
            var entities = archChunk.GetNativeArray(EntityType);
            var entity = entities[0];

            if(Vertices[entity].Length > 0)
            {
                //Debug.Log("had to clear");
                Vertices[entity].Clear();
                Tris[entity].Clear();
                Uvs[entity].Clear();
                Normals[entity].Clear();
            }

            //TODO try without any variables and compare

            //int xOff = 1;
            //int yOff = 256;
            //int zOff = 16;

            int vertIndex = 0;
            ushort x, y, z;
            int3 queriedChunkPos;
            Entity e;
            bool draw = false;
            ushort currBlock = 0;

            for(ushort i = 0; i < 4096; i++)
            {
                //skip air
                if(blocks[entity][i] == (transparent + 1))
                    continue;
                //x = i & 15;
                //y = i >> 8;
                //z = (i >> 4) & 15;
                MortonUtility.m3d_d_sLUT16(i, &x, &y, &z);
                //Solid cubical blocks are all under 2000
                //since i likely cant access static data about block types
                //some sort of inherent organization may be necessary for now
                //tris 6 others 4
                //unity is cw for front facing triangles

                //What is faster for checks, reassigning this value, or modifying it with math I wonder
                //also test with different check orders, make sure it is optimized

                currBlock = blocks[entity][i];
                //c# is dumb and returns an int when ~-ing ushorts so i have to take only the bits I need from that
                currBlock &= (~transparent & 0xFFFF);

                //left
                draw = false;
                if(x == 0)
                {
                    queriedChunkPos = chunk[entity].pos;
                    queriedChunkPos.x -= 1;
                    if(ChunkEntities.TryGetValue(queriedChunkPos, out e))
                    {
                        //hopefully length is always greater than 
                        //wrap x to 15, same y/z
                        //block is transparent next to this one, so we must draw
                        if(blocks[e][(int) MortonUtility.m3d_e_sLUT16((ushort) 15, y, z)].Value > transparent)
                            draw = true;
                    }
                    else
                    {
                        //there is no chunk next to this one, so draw (this is the outside)
                        draw = false;
                    }
                }
                else if(blocks[entity][(int) MortonUtility.m3d_e_sLUT16((ushort) (x - 1), y, z)].Value > transparent)
                {
                    draw = true;
                }

                if(draw)
                {
                    Normals[entity].Add(new float3(-1, 0, 0));
                    Normals[entity].Add(new float3(-1, 0, 0));
                    Normals[entity].Add(new float3(-1, 0, 0));
                    Normals[entity].Add(new float3(-1, 0, 0));

                    Vertices[entity].Add(new float3(x + 0, y + 0, z + 0));
                    Vertices[entity].Add(new float3(x + 0, y + 1, z + 0));
                    Vertices[entity].Add(new float3(x + 0, y + 1, z + 1));
                    Vertices[entity].Add(new float3(x + 0, y + 0, z + 1));

                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 1);
                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 3);
                    Tris[entity].Add(vertIndex + 2);

                    //I could use ternary to make it look cleaner but this saves on compares
                    if(currBlock == 0)
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock + 1));
                        Uvs[entity].Add(new float3(1, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 0, currBlock + 1));
                    }
                    else
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock));
                        Uvs[entity].Add(new float3(1, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 0, currBlock));
                    }
                    vertIndex += 4;
                }

                //right
                draw = false;
                if(x == 15)
                {
                    queriedChunkPos = chunk[entity].pos;
                    queriedChunkPos.x += 1;
                    if(ChunkEntities.TryGetValue(queriedChunkPos, out e))
                    {
                        if(blocks[e][(int) MortonUtility.m3d_e_sLUT16((ushort) 0, y, z)].Value > transparent)
                            draw = true;
                    }
                    else
                    {
                        draw = false;
                    }
                }
                else if(blocks[entity][(int) MortonUtility.m3d_e_sLUT16((ushort) (x + 1), y, z)].Value > transparent)
                {
                    draw = true;
                }

                if(draw)
                {
                    Normals[entity].Add(new float3(1, 0, 0));
                    Normals[entity].Add(new float3(1, 0, 0));
                    Normals[entity].Add(new float3(1, 0, 0));
                    Normals[entity].Add(new float3(1, 0, 0));

                    Vertices[entity].Add(new float3(x + 1, y + 0, z + 0));
                    Vertices[entity].Add(new float3(x + 1, y + 1, z + 0));
                    Vertices[entity].Add(new float3(x + 1, y + 1, z + 1));
                    Vertices[entity].Add(new float3(x + 1, y + 0, z + 1));

                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 1);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 3);

                    if(currBlock == 0)
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock + 1));
                        Uvs[entity].Add(new float3(1, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 0, currBlock + 1));
                    }
                    else
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock));
                        Uvs[entity].Add(new float3(1, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 0, currBlock));
                    }
                    vertIndex += 4;
                }

                //back
                draw = false;
                if(z == 0)
                {
                    queriedChunkPos = chunk[entity].pos;
                    queriedChunkPos.z -= 1;
                    if(ChunkEntities.TryGetValue(queriedChunkPos, out e))
                    {
                        if(blocks[e][(int) MortonUtility.m3d_e_sLUT16((ushort) x, y, 15)].Value > transparent)
                            draw = true;
                    }
                    else
                    {
                        draw = false;
                    }
                }
                else if(blocks[entity][(int) MortonUtility.m3d_e_sLUT16(x, y, (ushort) (z - 1))].Value > transparent)
                {
                    draw = true;
                }

                if(draw)
                {
                    Normals[entity].Add(new float3(0, 0, -1));
                    Normals[entity].Add(new float3(0, 0, -1));
                    Normals[entity].Add(new float3(0, 0, -1));
                    Normals[entity].Add(new float3(0, 0, -1));

                    Vertices[entity].Add(new float3(x + 0, y + 0, z + 0));
                    Vertices[entity].Add(new float3(x + 0, y + 1, z + 0));
                    Vertices[entity].Add(new float3(x + 1, y + 1, z + 0));
                    Vertices[entity].Add(new float3(x + 1, y + 0, z + 0));

                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 1);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 3);

                    if(currBlock == 0)
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock + 1));
                        Uvs[entity].Add(new float3(1, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 0, currBlock + 1));
                    }
                    else
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock));
                        Uvs[entity].Add(new float3(1, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 0, currBlock));
                    }
                    vertIndex += 4;
                }

                //front
                draw = false;
                if(z == 15)
                {
                    queriedChunkPos = chunk[entity].pos;
                    queriedChunkPos.z += 1;
                    if(ChunkEntities.TryGetValue(queriedChunkPos, out e))
                    {
                        if(blocks[e][(int) MortonUtility.m3d_e_sLUT16((ushort) x, y, 0)].Value > transparent)
                            draw = true;
                    }
                    else
                    {
                        draw = false;
                    }
                }
                else if(blocks[entity][(int) MortonUtility.m3d_e_sLUT16(x, y, (ushort) (z + 1))].Value > transparent)
                {
                    draw = true;
                }

                if(draw)
                {
                    Normals[entity].Add(new float3(0, 0, 1));
                    Normals[entity].Add(new float3(0, 0, 1));
                    Normals[entity].Add(new float3(0, 0, 1));
                    Normals[entity].Add(new float3(0, 0, 1));

                    Vertices[entity].Add(new float3(x + 0, y + 0, z + 1));
                    Vertices[entity].Add(new float3(x + 0, y + 1, z + 1));
                    Vertices[entity].Add(new float3(x + 1, y + 1, z + 1));
                    Vertices[entity].Add(new float3(x + 1, y + 0, z + 1));

                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 1);
                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 3);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 0);

                    if(currBlock == 0)
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock + 1));
                        Uvs[entity].Add(new float3(1, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 1, currBlock + 1));
                        Uvs[entity].Add(new float3(0, 0, currBlock + 1));
                    }
                    else
                    {
                        Uvs[entity].Add(new float3(1, 0, currBlock));
                        Uvs[entity].Add(new float3(1, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 0, currBlock));
                    }
                    vertIndex += 4;
                }

                //below
                draw = false;
                if(y == 0)
                {
                    if(chunk[entity].pos.y == 0)
                    {
                        draw = false;
                    }
                    else
                    {
                        queriedChunkPos = chunk[entity].pos;
                        queriedChunkPos.y -= 1;
                        if(ChunkEntities.TryGetValue(queriedChunkPos, out e))
                        {
                            if(blocks[e][(int) MortonUtility.m3d_e_sLUT16((ushort) x, 15, z)].Value > transparent)
                                draw = true;
                        }
                        else
                        {
                            draw = false;
                        }
                    }
                }
                else if(blocks[entity][(int) MortonUtility.m3d_e_sLUT16(x, (ushort) (y - 1), z)].Value > transparent)
                {
                    draw = true;
                }

                if(draw)
                {
                    Normals[entity].Add(new float3(0, -1, 0));
                    Normals[entity].Add(new float3(0, -1, 0));
                    Normals[entity].Add(new float3(0, -1, 0));
                    Normals[entity].Add(new float3(0, -1, 0));

                    Vertices[entity].Add(new float3(x + 0, y + 0, z + 0));
                    Vertices[entity].Add(new float3(x + 0, y + 0, z + 1));
                    Vertices[entity].Add(new float3(x + 1, y + 0, z + 1));
                    Vertices[entity].Add(new float3(x + 1, y + 0, z + 0));

                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 1);
                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 3);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 0);

                    if(currBlock == 0)
                    {
                        Uvs[entity].Add(new float3(0, 0, currBlock + 2));
                        Uvs[entity].Add(new float3(1, 0, currBlock + 2));
                        Uvs[entity].Add(new float3(1, 1, currBlock + 2));
                        Uvs[entity].Add(new float3(0, 1, currBlock + 2));
                    }
                    else
                    {
                        Uvs[entity].Add(new float3(0, 0, currBlock));
                        Uvs[entity].Add(new float3(1, 0, currBlock));
                        Uvs[entity].Add(new float3(1, 1, currBlock));
                        Uvs[entity].Add(new float3(0, 1, currBlock));
                    }
                    vertIndex += 4;
                }

                //above
                if(y == 15)
                {
                    queriedChunkPos = chunk[entity].pos;
                    queriedChunkPos.y += 1;
                    if(ChunkEntities.TryGetValue(queriedChunkPos, out e))
                    {
                        if(blocks[e][(int) MortonUtility.m3d_e_sLUT16((ushort) x, 0, z)].Value > transparent)
                            draw = true;
                    }
                    else
                    {
                        draw = false;
                    }
                }
                else if(blocks[entity][(int) MortonUtility.m3d_e_sLUT16(x, (ushort) (y + 1), z)].Value > transparent)
                {
                    draw = true;
                }

                if(draw)
                {
                    Normals[entity].Add(new float3(0, 1, 0));
                    Normals[entity].Add(new float3(0, 1, 0));
                    Normals[entity].Add(new float3(0, 1, 0));
                    Normals[entity].Add(new float3(0, 1, 0));

                    Vertices[entity].Add(new float3(x + 0, y + 1, z + 0));
                    Vertices[entity].Add(new float3(x + 0, y + 1, z + 1));
                    Vertices[entity].Add(new float3(x + 1, y + 1, z + 1));
                    Vertices[entity].Add(new float3(x + 1, y + 1, z + 0));

                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 1);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 0);
                    Tris[entity].Add(vertIndex + 2);
                    Tris[entity].Add(vertIndex + 3);

                    Uvs[entity].Add(new float3(0, 0, currBlock));
                    Uvs[entity].Add(new float3(1, 0, currBlock));
                    Uvs[entity].Add(new float3(1, 1, currBlock));
                    Uvs[entity].Add(new float3(0, 1, currBlock));
                    vertIndex += 4;
                }
            }
            
            //meshDirty_in.Dirty = true;
            // = new MeshDirty { Entity = entity, Dirty = true };
            CommandBuffer.AddComponent<MeshDirty>(chunkIndex, entity, new MeshDirty { Entity = entity });
        }
    }
}