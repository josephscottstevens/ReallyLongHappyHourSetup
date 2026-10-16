using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.Game.ObjectBuilders.Components;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace OsmiumAlienWorld
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class WispAtmosphere : MySessionComponentBase
    {
        const ushort PacketId = 54821;
        const string ParticleName = "ExhaustElectric";
        const string PlanetSubtype = "Alien";

        const double TickDt = 1.0 / 60.0;
        // Wisps exist this far from a player. The particle itself only draws to about 3 km.
        const double PlayerRange = 8500.0;
        const double PlayerRangeSq = PlayerRange * PlayerRange;
        const double ViewRange = 2900.0;
        const double ViewRangeSq = ViewRange * ViewRange;
        // From a third of the way up the air to just under the ceiling.
        const double BandInner = 0.35;
        const double BandOuter = 0.98;
        const double ChaseRange = 1800.0;
        const double AltitudeChaseSpeed = 50.0;
        const double TerrainClearance = 50.0;
        const int TargetRefreshTicks = 20;
        const int StormGraceTicks = 600;
        const int WispsPerPlayer = 30;
        const int MaxWisps = 64;
        const float WispRadius = 22f;
        const float DamagePerSecond = 1200f;
        const int MaxBlocksPerHit = 8;
        const int DamageIntervalTicks = 30;
        const int NetIntervalTicks = 10;
        const int SpawnIntervalTicks = 45;
        const int PlanetRefreshTicks = 600;
        const double TrailSpacing = 7.0;
        const double TrailSpacingSq = TrailSpacing * TrailSpacing;
        const int TrailLifeTicks = 100;
        const int SplashLifeTicks = 50;
        const int FleetingCap = 480;
        const float HumRange = 220f;
        const double HumRangeSq = 220.0 * 220.0;
        const int MaxHums = 3;
        const string HumCue = "OsmiumWispHum";
        static readonly Vector4 WispGreen = new Vector4(0.8f, 3.6f, 1.25f, 1f);

        readonly List<Wisp> _wisps = new List<Wisp>();
        readonly Dictionary<int, RemoteWisp> _remote = new Dictionary<int, RemoteWisp>();
        readonly Dictionary<int, MyParticleEffect> _effects = new Dictionary<int, MyParticleEffect>();
        readonly Dictionary<int, Vector3D> _trailAnchor = new Dictionary<int, Vector3D>();
        readonly List<FleetingEffect> _fleeting = new List<FleetingEffect>();
        readonly List<Vector3D> _pendingSplashes = new List<Vector3D>();
        readonly HashSet<IMyEntity> _planets = new HashSet<IMyEntity>();
        readonly List<IMyPlayer> _players = new List<IMyPlayer>();
        readonly Dictionary<long, Vector3D> _playerPos = new Dictionary<long, Vector3D>();
        readonly Dictionary<long, Vector3D> _playerVel = new Dictionary<long, Vector3D>();
        readonly List<long> _dropIds = new List<long>();
        readonly Dictionary<int, MyEntity3DSoundEmitter> _hums = new Dictionary<int, MyEntity3DSoundEmitter>();
        readonly HumCandidate[] _humBest = new HumCandidate[MaxHums];
        readonly List<int> _humDrop = new List<int>();
        MySoundPair _humPair;
        int _humCount;
        readonly Random _random = new Random();

        MyStringHash _damageType;
        bool _damageTypeReady;
        bool _booted;
        bool _announced;
        bool _stormAnnounced;
        bool _particleWarned;
        int _nextHitNotice;
        int _nextId = 1;
        int _tick;
        int _spawnCooldown;
        int _particleCooldown;

        public override void LoadData()
        {
            MyAPIGateway.Multiplayer.RegisterMessageHandler(PacketId, OnPacket);
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PacketId, OnPacket);
            ClearParticles();
            _wisps.Clear();
            _remote.Clear();
            _planets.Clear();
        }

        public override void UpdateAfterSimulation()
        {
            if (MyAPIGateway.Session == null)
                return;

            if (!_booted)
            {
                _booted = true;
                if (!MyAPIGateway.Utilities.IsDedicated)
                    MyAPIGateway.Utilities.ShowNotification("Osmium wisps loaded. Fly to the top of the Alien atmosphere.", 10000, "White");
            }

            if (MyAPIGateway.Session.IsServer)
                SimulateServer();

            if (!MyAPIGateway.Utilities.IsDedicated)
                UpdateParticles();
        }

        void SimulateServer()
        {
            _tick++;
            if (_tick % PlanetRefreshTicks == 1)
                RefreshPlanets();

            CollectPlayers();
            TrackPlayers();
            MoveWisps();
            RemoveDistantWisps();

            if (_spawnCooldown > 0)
                _spawnCooldown--;
            else
            {
                _spawnCooldown = SpawnIntervalTicks;
                TopUpWisps();
            }

            if (_tick % DamageIntervalTicks == 0)
                DamageWisps();

            if (!_announced && _wisps.Count > 0 && !MyAPIGateway.Utilities.IsDedicated)
            {
                _announced = true;
                MyAPIGateway.Utilities.ShowNotification("The upper atmosphere is carrying osmium wisps.", 8000, "White");
            }

            if (_tick % NetIntervalTicks == 0)
                Broadcast();
        }

        void RefreshPlanets()
        {
            _planets.Clear();
            MyAPIGateway.Entities.GetEntities(_planets, IsAlienPlanet);
        }

        static bool IsAlienPlanet(IMyEntity entity)
        {
            MyPlanet planet = entity as MyPlanet;
            if (planet == null || planet.MarkedForClose || !planet.HasAtmosphere || planet.AtmosphereAltitude < 1f)
                return false;
            if (planet.Generator == null)
                return false;
            return planet.Generator.Id.SubtypeName == PlanetSubtype;
        }

        void CollectPlayers()
        {
            _players.Clear();
            MyAPIGateway.Players.GetPlayers(_players, IsHuman);
        }

        static bool IsHuman(IMyPlayer player)
        {
            return player != null && !player.IsBot;
        }

        void TrackPlayers()
        {
            _dropIds.Clear();
            foreach (long id in _playerPos.Keys)
                _dropIds.Add(id);

            for (int i = 0; i < _players.Count; i++)
            {
                IMyPlayer player = _players[i];
                long id = player.IdentityId;
                Vector3D pos = player.GetPosition();
                Vector3D prev;
                if (_playerPos.TryGetValue(id, out prev))
                    _playerVel[id] = (pos - prev) / TickDt;
                else
                    _playerVel[id] = Vector3D.Zero;
                _playerPos[id] = pos;
                _dropIds.Remove(id);
            }

            for (int i = 0; i < _dropIds.Count; i++)
            {
                _playerPos.Remove(_dropIds[i]);
                _playerVel.Remove(_dropIds[i]);
            }
        }

        void MoveWisps()
        {
            for (int i = 0; i < _wisps.Count; i++)
            {
                Wisp wisp = _wisps[i];
                MyPlanet planet = wisp.Planet;
                if (planet == null || planet.MarkedForClose)
                    continue;

                Vector3D center = planet.WorldMatrix.Translation;
                Vector3D radial = wisp.Position - center;
                if (radial.LengthSquared() < 1.0)
                    radial = new Vector3D(0, 1, 0);
                radial = Vector3D.Normalize(radial);

                bool allowAnyAltitude = wisp.StormGrace > 0 || IsStormAt(wisp.Position);
                if (_tick >= wisp.NextTargetTick)
                {
                    wisp.NextTargetTick = _tick + TargetRefreshTicks;
                    long targetId;
                    if (TryNearestPoweredGrid(planet, wisp.Position, ChaseRange, allowAnyAltitude, out targetId))
                        wisp.TargetId = targetId;
                    else
                        wisp.TargetId = 0;
                }

                Vector3D aim;
                if (wisp.TargetId != 0 && TryAimPoint(wisp.TargetId, wisp.Position, out aim) && (allowAnyAltitude || IsStormAt(aim) || GridStillInBand(planet, wisp.TargetId)))
                {
                    if (!allowAnyAltitude && !IsStormAt(aim) && !CanReachAltitude(planet, aim))
                    {
                        double clamped = Clamp((aim - center).Length(), BandInnerRadius(planet), BandOuterRadius(planet));
                        Vector3D offset = aim - center;
                        if (offset.LengthSquared() > 1.0)
                            aim = center + Vector3D.Normalize(offset) * clamped;
                    }

                    wisp.HasTarget = true;
                    wisp.Target = aim;
                }
                else
                {
                    wisp.HasTarget = false;
                    wisp.TargetId = 0;
                }

                if (!wisp.HasTarget && wisp.WasChasing)
                    wisp.NextWanderTick = _tick;
                wisp.WasChasing = wisp.HasTarget;

                wisp.InStorm = wisp.StormGrace > 0 || IsStormAt(wisp.Position) || (wisp.HasTarget && IsStormAt(wisp.Target));

                if (wisp.StormGrace > 0)
                    wisp.StormGrace--;

                Vector3D heading = wisp.Heading - radial * Vector3D.Dot(wisp.Heading, radial);
                if (heading.LengthSquared() < 0.0001)
                    heading = Vector3D.CalculatePerpendicularVector(radial);
                heading = Vector3D.Normalize(heading);

                if (wisp.HasTarget)
                {
                    double turnRate = ApproachTarget(wisp, heading, radial, center);
                    SteerToward(ref heading, wisp.Target - wisp.Position, radial, turnRate * TickDt);
                }
                else
                {
                    if (_tick >= wisp.NextWanderTick)
                        PickWander(wisp, planet);

                    double turn = wisp.TurnRate * TickDt;
                    Vector3D bitangent = Vector3D.Cross(radial, heading);
                    if (bitangent.LengthSquared() > 0.0001)
                        heading = Vector3D.Normalize(heading * Math.Cos(turn) + Vector3D.Normalize(bitangent) * Math.Sin(turn));

                    SeparateFromWisps(i, ref heading, radial);
                    DriftAltitude(wisp, planet);
                    EaseSpeed(wisp, wisp.WanderSpeed, 22.0);
                }

                UpdateAltitude(wisp, planet, center);
                AvoidTerrain(planet, center, radial, ref heading, wisp);

                Vector3D next = wisp.Position + heading * (wisp.Speed * TickDt);
                next = center + Vector3D.Normalize(next - center) * wisp.Altitude;
                KeepAboveTerrain(planet, center, ref next, ref wisp.Altitude);

                wisp.Heading = heading;
                wisp.Position = next;
                wisp.Velocity = heading * wisp.Speed;
            }
        }

        void PickWander(Wisp wisp, MyPlanet planet)
        {
            double mag = 0.28 + _random.NextDouble() * 0.62;
            wisp.TurnRate = _random.NextDouble() < 0.5 ? -mag : mag;
            wisp.WanderSpeed = 50.0 + _random.NextDouble() * 75.0;
            double inner = wisp.InStorm ? planet.AverageRadius + 80.0 : BandInnerRadius(planet);
            double outer = BandOuterRadius(planet);
            double reach = (outer - inner) * (0.12 + _random.NextDouble() * 0.38);
            wisp.WanderAltitude = Clamp(wisp.Altitude + (_random.NextDouble() * 2.0 - 1.0) * reach, inner, outer);
            wisp.NextWanderTick = _tick + 200 + _random.Next(280);
        }

        void DriftAltitude(Wisp wisp, MyPlanet planet)
        {
            double inner = wisp.InStorm ? planet.AverageRadius + 80.0 : BandInnerRadius(planet);
            double outer = BandOuterRadius(planet);
            double goal = Clamp(wisp.WanderAltitude, inner, outer);
            double step = 16.0 * TickDt;
            if (wisp.Altitude < goal)
                wisp.Altitude = Math.Min(goal, wisp.Altitude + step);
            else
                wisp.Altitude = Math.Max(goal, wisp.Altitude - step);
        }

        static void EaseSpeed(Wisp wisp, double desired, double accel)
        {
            double step = accel * TickDt;
            if (wisp.Speed < desired)
                wisp.Speed = Math.Min(desired, wisp.Speed + step);
            else
                wisp.Speed = Math.Max(desired, wisp.Speed - step);
        }

        void SeparateFromWisps(int index, ref Vector3D heading, Vector3D radial)
        {
            const double range = 160.0;
            const double rangeSq = range * range;
            Wisp wisp = _wisps[index];
            Vector3D push = Vector3D.Zero;
            for (int i = 0; i < _wisps.Count; i++)
            {
                if (i == index)
                    continue;
                Vector3D delta = wisp.Position - _wisps[i].Position;
                double distSq = delta.LengthSquared();
                if (distSq >= rangeSq || distSq < 1.0)
                    continue;
                double dist = Math.Sqrt(distSq);
                push += delta / dist * (1.0 - dist / range);
            }

            if (push.LengthSquared() < 0.0004)
                return;
            SteerToward(ref heading, push, radial, 1.4 * TickDt);
        }

        void RemoveDistantWisps()
        {
            for (int i = _wisps.Count - 1; i >= 0; i--)
            {
                Wisp wisp = _wisps[i];
                if (wisp.Planet == null || wisp.Planet.MarkedForClose || !AnyPlayerWithin(wisp.Position, PlayerRangeSq))
                    _wisps.RemoveAt(i);
            }
        }

        void TopUpWisps()
        {
            if (_wisps.Count >= MaxWisps)
                return;

            for (int p = 0; p < _players.Count && _wisps.Count < MaxWisps; p++)
            {
                    Vector3D playerPos = _players[p].GetPosition();
                    Vector3D velocity;
                    if (!_playerVel.TryGetValue(_players[p].IdentityId, out velocity))
                        velocity = Vector3D.Zero;
                foreach (IMyEntity entity in _planets)
                {
                    if (_wisps.Count >= MaxWisps)
                        return;

                    MyPlanet planet = entity as MyPlanet;
                    if (planet == null || !IsBelowCeiling(planet, playerPos))
                        continue;

                    bool storm = IsStormAt(playerPos);
                    if (!storm && !IsInUpperAtmosphere(planet, playerPos))
                        continue;

                    int nearby = CountWispsNear(playerPos, PlayerRangeSq);
                    int need = WispsPerPlayer - nearby;
                    for (int n = 0; n < need && _wisps.Count < MaxWisps; n++)
                        _wisps.Add(CreateWisp(planet, playerPos, velocity, storm));
                }
            }
        }

        Wisp CreateWisp(MyPlanet planet, Vector3D playerPos, Vector3D velocity, bool storm)
        {
            Vector3D center = planet.WorldMatrix.Translation;
            Vector3D radial = playerPos - center;
            if (radial.LengthSquared() < 1.0)
                radial = new Vector3D(0, 1, 0);
            radial = Vector3D.Normalize(radial);

            Vector3D tangent = Vector3D.CalculatePerpendicularVector(radial);
            Vector3D bitangent = Vector3D.Normalize(Vector3D.Cross(radial, tangent));

            Vector3D travel = velocity - radial * Vector3D.Dot(velocity, radial);
            bool moving = travel.LengthSquared() > 150.0 * 150.0;
            double roll = _random.NextDouble();
            double angle;
            double arc;
            if (roll < 0.45)
            {
                angle = _random.NextDouble() * Math.PI * 2.0;
                arc = 1800.0 + _random.NextDouble() * 1000.0;
            }
            else if (roll < 0.78)
            {
                arc = 3400.0 + _random.NextDouble() * 4600.0;
                if (moving)
                {
                    Vector3D forward = Vector3D.Normalize(travel);
                    double ahead = Math.Atan2(Vector3D.Dot(forward, bitangent), Vector3D.Dot(forward, tangent));
                    angle = ahead + (_random.NextDouble() * 2.0 - 1.0) * 0.9;
                }
                else
                {
                    angle = _random.NextDouble() * Math.PI * 2.0;
                }
            }
            else
            {
                angle = _random.NextDouble() * Math.PI * 2.0;
                arc = 700.0 + _random.NextDouble() * 1000.0;
            }

            double inner = BandInnerRadius(planet);
            double outer = BandOuterRadius(planet);
            double playerAlt = Clamp((playerPos - center).Length(), inner, outer);
            double altitude;
            if (storm)
            {
                altitude = playerAlt + (_random.NextDouble() * 2.0 - 1.0) * 400.0;
                altitude = Clamp(altitude, planet.AverageRadius + 80.0, outer);
            }
            else if (_random.NextDouble() < 0.55)
            {
                double spread = (outer - inner) * 0.18;
                altitude = playerAlt + (_random.NextDouble() * 2.0 - 1.0) * spread;
            }
            else
            {
                altitude = inner + _random.NextDouble() * (outer - inner);
            }
            altitude = Clamp(altitude, storm ? planet.AverageRadius + 80.0 : inner, outer);

            Vector3D rough = center + radial * altitude + tangent * (Math.Cos(angle) * arc) + bitangent * (Math.Sin(angle) * arc);
            Vector3D position = center + Vector3D.Normalize(rough - center) * altitude;
            KeepAboveTerrain(planet, center, ref position, ref altitude);
            altitude = Clamp(altitude, storm ? planet.AverageRadius + 80.0 : inner, outer);
            position = center + Vector3D.Normalize(position - center) * altitude;

            Vector3D wispRadial = position - center;
            if (wispRadial.LengthSquared() < 1.0)
                wispRadial = radial;
            wispRadial = Vector3D.Normalize(wispRadial);

            Vector3D wispTangent = Vector3D.CalculatePerpendicularVector(wispRadial);
            Vector3D wispBitangent = Vector3D.Normalize(Vector3D.Cross(wispRadial, wispTangent));
            double headingAngle = _random.NextDouble() * Math.PI * 2.0;
            Vector3D heading = Vector3D.Normalize(wispTangent * Math.Cos(headingAngle) + wispBitangent * Math.Sin(headingAngle));

            long aimId = 0;
            Vector3D aim = position;
            bool aimed = TryNearestPoweredGrid(planet, position, ChaseRange, storm, out aimId) && TryAimPoint(aimId, position, out aim);
            if (aimed)
            {
                Vector3D toGrid = aim - position;
                toGrid -= wispRadial * Vector3D.Dot(toGrid, wispRadial);
                if (toGrid.LengthSquared() > 1.0)
                {
                    heading = Vector3D.Normalize(toGrid);
                    double spread = (_random.NextDouble() * 2.0 - 1.0) * 0.35;
                    Vector3D side = Vector3D.Cross(wispRadial, heading);
                    heading = Vector3D.Normalize(heading * Math.Cos(spread) + side * Math.Sin(spread));
                }
            }

            if (storm && !_stormAnnounced && !MyAPIGateway.Utilities.IsDedicated)
            {
                _stormAnnounced = true;
                MyAPIGateway.Utilities.ShowNotification("The storm is dragging osmium wisps down through the atmosphere.", 8000, "White");
            }

            Wisp wisp = new Wisp();
            wisp.Id = _nextId++;
            wisp.Planet = planet;
            wisp.Position = position;
            wisp.Altitude = altitude;
            wisp.Heading = heading;
            wisp.Speed = 70.0;
            wisp.Velocity = heading * wisp.Speed;
            wisp.HasTarget = aimed;
            wisp.TargetId = aimed ? aimId : 0;
            wisp.Target = aim;
            wisp.InStorm = storm;
            wisp.StormGrace = storm ? StormGraceTicks : 0;
            wisp.NextTargetTick = _tick + TargetRefreshTicks;
            PickWander(wisp, planet);
            wisp.Speed = wisp.WanderSpeed;
            wisp.Velocity = heading * wisp.Speed;
            return wisp;
        }

        void DamageWisps()
        {
            EnsureDamageType();
            float amount = (float)(DamagePerSecond * (DamageIntervalTicks * TickDt));

            for (int i = 0; i < _wisps.Count; i++)
            {
                Wisp wisp = _wisps[i];
                BoundingSphereD sphere = new BoundingSphereD(wisp.Position, WispRadius);
                List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
                if (entities == null)
                    continue;

                for (int e = 0; e < entities.Count; e++)
                {
                    IMyCubeGrid grid = entities[e] as IMyCubeGrid;
                    if (grid == null || grid.MarkedForClose || IsDamageBlocked(grid.GetPosition()))
                        continue;

                    BoundingSphereD gridSphere = sphere;
                    List<IMySlimBlock> blocks = grid.GetBlocksInsideSphere(ref gridSphere);
                    if (blocks == null || blocks.Count == 0)
                        continue;

                    if (DamageClosest(grid, blocks, wisp.Position, amount) > 0 && _tick >= _nextHitNotice && !MyAPIGateway.Utilities.IsDedicated)
                    {
                        _nextHitNotice = _tick + 180;
                        MyAPIGateway.Utilities.ShowNotification("An osmium wisp is eating the hull.", 3000, "White");
                    }
                }
            }
        }

        int DamageClosest(IMyCubeGrid grid, List<IMySlimBlock> blocks, Vector3D origin, float amount)
        {
            IMySlimBlock[] chosen = new IMySlimBlock[MaxBlocksPerHit];
            double[] chosenDist = new double[MaxBlocksPerHit];
            int count = 0;

            for (int i = 0; i < blocks.Count; i++)
            {
                IMySlimBlock block = blocks[i];
                IMyDestroyableObject destroyable = block as IMyDestroyableObject;
                if (destroyable == null || destroyable.Integrity <= 0f)
                    continue;

                Vector3D blockCenter;
                block.ComputeWorldCenter(out blockCenter);
                double distance = Vector3D.DistanceSquared(blockCenter, origin);

                if (count < MaxBlocksPerHit)
                {
                    chosen[count] = block;
                    chosenDist[count] = distance;
                    count++;
                    continue;
                }

                int farthest = 0;
                for (int c = 1; c < count; c++)
                {
                    if (chosenDist[c] > chosenDist[farthest])
                        farthest = c;
                }

                if (distance < chosenDist[farthest])
                {
                    chosen[farthest] = block;
                    chosenDist[farthest] = distance;
                }
            }

            for (int i = 0; i < count; i++)
            {
                IMyDestroyableObject destroyable = chosen[i] as IMyDestroyableObject;
                if (destroyable == null)
                    continue;
                destroyable.DoDamage(amount, _damageType, true, null, 0L, grid.EntityId, false, null);
                Vector3D hit;
                chosen[i].ComputeWorldCenter(out hit);
                _pendingSplashes.Add(hit);
                SpawnSplash(hit);
            }

            return count;
        }

        void Broadcast()
        {
            WispPacket packet = new WispPacket();
            for (int i = 0; i < _wisps.Count; i++)
            {
                Wisp wisp = _wisps[i];
                WispNetState state = new WispNetState();
                state.Id = wisp.Id;
                state.X = wisp.Position.X;
                state.Y = wisp.Position.Y;
                state.Z = wisp.Position.Z;
                state.VX = wisp.Velocity.X;
                state.VY = wisp.Velocity.Y;
                state.VZ = wisp.Velocity.Z;
                packet.Wisps.Add(state);
            }

            for (int i = 0; i < _pendingSplashes.Count; i++)
            {
                Vector3D splash = _pendingSplashes[i];
                WispNetState state = new WispNetState();
                state.X = splash.X;
                state.Y = splash.Y;
                state.Z = splash.Z;
                packet.Splashes.Add(state);
            }
            _pendingSplashes.Clear();

            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(packet);
            MyAPIGateway.Multiplayer.SendMessageToOthers(PacketId, data, true);
        }

        void OnPacket(byte[] data)
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.IsServer || data == null)
                return;

            WispPacket packet = MyAPIGateway.Utilities.SerializeFromBinary<WispPacket>(data);
            if (packet == null)
                return;

            if (packet.Splashes != null)
            {
                for (int i = 0; i < packet.Splashes.Count; i++)
                {
                    WispNetState splash = packet.Splashes[i];
                    SpawnSplash(new Vector3D(splash.X, splash.Y, splash.Z));
                }
            }

            if (packet.Wisps == null)
                return;

            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < packet.Wisps.Count; i++)
            {
                WispNetState state = packet.Wisps[i];
                seen.Add(state.Id);

                RemoteWisp remote;
                if (!_remote.TryGetValue(state.Id, out remote))
                {
                    remote = new RemoteWisp();
                    _remote.Add(state.Id, remote);
                }

                remote.Origin = new Vector3D(state.X, state.Y, state.Z);
                remote.Velocity = new Vector3D(state.VX, state.VY, state.VZ);
                remote.AgeTicks = 0;
            }

            List<int> remove = new List<int>();
            foreach (KeyValuePair<int, RemoteWisp> pair in _remote)
            {
                if (!seen.Contains(pair.Key))
                    remove.Add(pair.Key);
            }

            for (int i = 0; i < remove.Count; i++)
                _remote.Remove(remove[i]);
        }

        void UpdateParticles()
        {
            if (_particleCooldown > 0)
                _particleCooldown--;

            UpdateFleeting();

            if (MyAPIGateway.Session.IsServer)
                SyncParticlesFromServer();
            else
                SyncParticlesFromRemote();

            UpdateHums();
        }

        void SyncParticlesFromServer()
        {
            HashSet<int> live = new HashSet<int>();
            for (int i = 0; i < _wisps.Count; i++)
            {
                Wisp wisp = _wisps[i];
                if (!WithinView(wisp.Position))
                    continue;
                live.Add(wisp.Id);
                PlaceEffect(wisp.Id, wisp.Position, wisp.Velocity);
            }

            RemoveStaleEffects(live);
        }

        void SyncParticlesFromRemote()
        {
            HashSet<int> live = new HashSet<int>();
            foreach (KeyValuePair<int, RemoteWisp> pair in _remote)
            {
                RemoteWisp remote = pair.Value;
                remote.AgeTicks++;
                if (remote.AgeTicks > 180)
                    continue;

                Vector3D position = remote.Origin + remote.Velocity * (remote.AgeTicks * TickDt);
                if (!WithinView(position))
                    continue;

                live.Add(pair.Key);
                PlaceEffect(pair.Key, position, remote.Velocity);
            }

            RemoveStaleEffects(live);
        }

        void PlaceEffect(int id, Vector3D position, Vector3D velocity)
        {
            MyParticleEffect effect;
            if (!_effects.TryGetValue(id, out effect) || effect == null || effect.IsStopped)
            {
                if (_particleCooldown > 0)
                    return;

                Vector3D forward = velocity.LengthSquared() > 1.0 ? Vector3D.Normalize(velocity) : new Vector3D(0, 0, -1);
                Vector3D up = Vector3D.CalculatePerpendicularVector(forward);
                MatrixD matrix = MatrixD.CreateWorld(position, forward, up);
                if (!MyParticlesManager.TryCreateParticleEffect(ParticleName, ref matrix, ref position, 0, out effect))
                {
                    _particleCooldown = 120;
                    if (!_particleWarned)
                    {
                        _particleWarned = true;
                        MyAPIGateway.Utilities.ShowNotification("Osmium wisp particle failed to spawn: " + ParticleName, 8000, "White");
                    }
                    return;
                }

                effect.UserScale = 3.6f;
                effect.UserRadiusMultiplier = 2.6f;
                effect.UserBirthMultiplier = 2.4f;
                effect.UserColorMultiplier = WispGreen;
                _effects[id] = effect;
            }

            Vector3D forwardNow = velocity.LengthSquared() > 1.0 ? Vector3D.Normalize(velocity) : new Vector3D(0, 0, -1);
            Vector3D upNow = Vector3D.CalculatePerpendicularVector(forwardNow);
            effect.WorldMatrix = MatrixD.CreateWorld(position, forwardNow, upNow);
            effect.SetTranslation(position);
            effect.Velocity = (Vector3)(forwardNow * -22.0);
            ConsiderTrail(id, position);
        }

        void RemoveStaleEffects(HashSet<int> live)
        {
            List<int> remove = new List<int>();
            foreach (KeyValuePair<int, MyParticleEffect> pair in _effects)
            {
                if (!live.Contains(pair.Key))
                    remove.Add(pair.Key);
            }

            for (int i = 0; i < remove.Count; i++)
            {
                MyParticleEffect effect;
                if (_effects.TryGetValue(remove[i], out effect) && effect != null)
                    MyParticlesManager.RemoveParticleEffect(effect);
                _effects.Remove(remove[i]);
                _trailAnchor.Remove(remove[i]);
            }
        }

        void ConsiderTrail(int id, Vector3D position)
        {
            Vector3D anchor;
            if (!_trailAnchor.TryGetValue(id, out anchor))
            {
                _trailAnchor[id] = position;
                return;
            }

            if (Vector3D.DistanceSquared(anchor, position) < TrailSpacingSq)
                return;

            _trailAnchor[id] = position;
            SpawnBurst(ParticleName, anchor, 2.2f, 1.7f, TrailLifeTicks);
        }

        void SpawnSplash(Vector3D position)
        {
            SpawnBurst("Damage_Electrical_Damaged", position, 2.2f, 1.6f, SplashLifeTicks);
        }

        void SpawnBurst(string name, Vector3D position, float scale, float radius, int lifeTicks)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            while (_fleeting.Count >= FleetingCap)
                ReleaseFleeting(0);

            Vector3D forward = new Vector3D(0, 1, 0);
            Vector3D up = new Vector3D(1, 0, 0);
            MatrixD matrix = MatrixD.CreateWorld(position, forward, up);
            MyParticleEffect effect;
            if (!MyParticlesManager.TryCreateParticleEffect(name, ref matrix, ref position, 0, out effect) || effect == null)
                return;

            effect.UserScale = scale;
            effect.UserRadiusMultiplier = radius;
            effect.UserBirthMultiplier = 2f;
            effect.UserColorMultiplier = WispGreen;
            effect.Velocity = Vector3.Zero;

            FleetingEffect fleeting = new FleetingEffect();
            fleeting.Effect = effect;
            fleeting.TicksLeft = lifeTicks;
            _fleeting.Add(fleeting);
        }

        void UpdateFleeting()
        {
            for (int i = _fleeting.Count - 1; i >= 0; i--)
            {
                FleetingEffect fleeting = _fleeting[i];
                fleeting.TicksLeft--;
                if (fleeting.TicksLeft == 20 && fleeting.Effect != null && !fleeting.Effect.IsStopped)
                    fleeting.Effect.Stop(false);
                if (fleeting.TicksLeft > 0)
                    continue;
                ReleaseFleeting(i);
            }
        }

        void ReleaseFleeting(int index)
        {
            FleetingEffect fleeting = _fleeting[index];
            if (fleeting.Effect != null)
                MyParticlesManager.RemoveParticleEffect(fleeting.Effect);
            _fleeting.RemoveAt(index);
        }

        void ClearParticles()
        {
            foreach (KeyValuePair<int, MyParticleEffect> pair in _effects)
            {
                if (pair.Value != null)
                    MyParticlesManager.RemoveParticleEffect(pair.Value);
            }
            _effects.Clear();
            _trailAnchor.Clear();
            for (int i = _fleeting.Count - 1; i >= 0; i--)
                ReleaseFleeting(i);
            SilenceHums();
        }

        void UpdateHums()
        {
            if (_humPair == null)
                _humPair = new MySoundPair(HumCue);

            IMyPlayer viewer = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.LocalHumanPlayer;
            if (viewer == null)
            {
                SilenceHums();
                return;
            }

            Vector3D ear = viewer.GetPosition();
            _humCount = 0;
            if (MyAPIGateway.Session.IsServer)
            {
                for (int i = 0; i < _wisps.Count; i++)
                    ConsiderHum(_wisps[i].Id, _wisps[i].Position, ear);
            }
            else
            {
                foreach (KeyValuePair<int, RemoteWisp> pair in _remote)
                {
                    RemoteWisp remote = pair.Value;
                    if (remote.AgeTicks > 180)
                        continue;
                    Vector3D position = remote.Origin + remote.Velocity * (remote.AgeTicks * TickDt);
                    ConsiderHum(pair.Key, position, ear);
                }
            }

            _humDrop.Clear();
            foreach (KeyValuePair<int, MyEntity3DSoundEmitter> pair in _hums)
            {
                bool keep = false;
                for (int i = 0; i < _humCount; i++)
                {
                    if (_humBest[i].Id == pair.Key)
                        keep = true;
                }
                if (!keep)
                    _humDrop.Add(pair.Key);
            }

            for (int i = 0; i < _humDrop.Count; i++)
                ReleaseHum(_humDrop[i]);

            for (int i = 0; i < _humCount; i++)
                PlaceHum(_humBest[i]);
        }

        void ConsiderHum(int id, Vector3D position, Vector3D ear)
        {
            double distSq = Vector3D.DistanceSquared(position, ear);
            if (distSq > HumRangeSq)
                return;

            int slot = _humCount;
            for (int i = 0; i < _humCount; i++)
            {
                if (distSq < _humBest[i].DistSq)
                {
                    slot = i;
                    break;
                }
            }

            if (slot >= MaxHums)
                return;

            int last = _humCount < MaxHums - 1 ? _humCount : MaxHums - 1;
            for (int i = last; i > slot; i--)
                _humBest[i] = _humBest[i - 1];

            HumCandidate candidate = new HumCandidate();
            candidate.Id = id;
            candidate.Position = position;
            candidate.DistSq = distSq;
            _humBest[slot] = candidate;
            if (_humCount < MaxHums)
                _humCount++;
        }

        void PlaceHum(HumCandidate candidate)
        {
            MyEntity3DSoundEmitter emitter;
            if (!_hums.TryGetValue(candidate.Id, out emitter) || emitter == null)
            {
                MyEntity anchor = null;
                IMyPlayer listener = MyAPIGateway.Session.LocalHumanPlayer;
                if (listener != null && listener.Controller != null && listener.Controller.ControlledEntity != null)
                    anchor = listener.Controller.ControlledEntity.Entity as MyEntity;
                emitter = new MyEntity3DSoundEmitter(anchor, false, 0f);
                emitter.Force3D = true;
                emitter.CustomMaxDistance = HumRange + 40f;
                emitter.CustomVolume = 1f;
                _hums[candidate.Id] = emitter;
            }

            emitter.SetPosition(candidate.Position);
            if (!emitter.IsPlaying)
                emitter.PlaySound(_humPair, true, false, false, true);
            double dist = Math.Sqrt(candidate.DistSq);
            float closeness = 1f - (float)(dist / HumRange);
            if (closeness < 0f)
                closeness = 0f;
            emitter.VolumeMultiplier = 0.22f + 0.78f * closeness;
            emitter.Update();
        }

        void ReleaseHum(int id)
        {
            MyEntity3DSoundEmitter emitter;
            if (_hums.TryGetValue(id, out emitter) && emitter != null)
            {
                emitter.StopSound(true);
                emitter.Cleanup();
            }
            _hums.Remove(id);
        }

        void SilenceHums()
        {
            _humDrop.Clear();
            foreach (int id in _hums.Keys)
                _humDrop.Add(id);
            for (int i = 0; i < _humDrop.Count; i++)
                ReleaseHum(_humDrop[i]);
        }

        struct HumCandidate
        {
            public int Id;
            public Vector3D Position;
            public double DistSq;
        }

        void EnsureDamageType()
        {
            if (_damageTypeReady)
                return;
            _damageType = MyStringHash.GetOrCompute("OsmiumWisp");
            _damageTypeReady = true;
        }

        bool AnyPlayerWithin(Vector3D position, double rangeSq)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (Vector3D.DistanceSquared(_players[i].GetPosition(), position) <= rangeSq)
                    return true;
            }
            return false;
        }

        bool WithinView(Vector3D position)
        {
            IMyPlayer viewer = MyAPIGateway.Session.LocalHumanPlayer;
            if (viewer == null)
                return AnyPlayerWithin(position, ViewRangeSq);
            return Vector3D.DistanceSquared(viewer.GetPosition(), position) <= ViewRangeSq;
        }

        int CountWispsNear(Vector3D position, double rangeSq)
        {
            int count = 0;
            for (int i = 0; i < _wisps.Count; i++)
            {
                if (Vector3D.DistanceSquared(_wisps[i].Position, position) <= rangeSq)
                    count++;
            }
            return count;
        }

        static bool IsInUpperAtmosphere(MyPlanet planet, Vector3D position)
        {
            double distance = (position - planet.WorldMatrix.Translation).Length();
            return distance >= BandInnerRadius(planet) && distance <= BandOuterRadius(planet);
        }

        static bool IsBelowCeiling(MyPlanet planet, Vector3D position)
        {
            double distance = (position - planet.WorldMatrix.Translation).Length();
            if (distance > BandOuterRadius(planet))
                return false;
            return planet.GetHeightFromSurface(position) > -30.0;
        }

        bool IsStormAt(Vector3D position)
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.WeatherEffects == null)
                return false;
            return IsStormName(MyAPIGateway.Session.WeatherEffects.GetWeather(position));
        }

        static bool IsStormName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.IndexOf("Storm", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Thunder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsDamageBlocked(Vector3D position)
        {
            return !MySessionComponentSafeZones.IsActionAllowed(position, MySafeZoneAction.Damage, 0L, 0UL);
        }

        static bool IsPowered(IMyCubeGrid grid)
        {
            IMyResourceDistributorComponent power = grid.ResourceDistributor;
            if (power == null)
                return false;
            VRage.MyResourceStateEnum state = power.ResourceState;
            return state == VRage.MyResourceStateEnum.Ok || state == VRage.MyResourceStateEnum.OverloadAdaptible;
        }

        bool TryNearestPoweredGrid(MyPlanet planet, Vector3D origin, double range, bool allowAnyAltitude, out long entityId)
        {
            entityId = 0;
            BoundingSphereD sphere = new BoundingSphereD(origin, range);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            if (entities == null)
                return false;

            double best = range * range;
            bool found = false;
            for (int i = 0; i < entities.Count; i++)
            {
                IMyCubeGrid grid = entities[i] as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose || !IsPowered(grid) || IsDamageBlocked(grid.GetPosition()))
                    continue;
                if (!allowAnyAltitude && !IsStormAt(grid.GetPosition()) && !GridOverlapsBand(planet, grid))
                    continue;

                double distance = Vector3D.DistanceSquared(grid.GetPosition(), origin);
                if (distance >= best)
                    continue;

                best = distance;
                entityId = grid.EntityId;
                found = true;
            }

            return found;
        }

        static bool GridStillInBand(MyPlanet planet, long entityId)
        {
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(entityId, out entity))
                return false;
            IMyCubeGrid grid = entity as IMyCubeGrid;
            return grid != null && GridOverlapsBand(planet, grid);
        }

        static bool CanReachAltitude(MyPlanet planet, Vector3D aim)
        {
            double altitude = (aim - planet.WorldMatrix.Translation).Length();
            return altitude >= BandInnerRadius(planet) && altitude <= BandOuterRadius(planet);
        }

        static bool GridOverlapsBand(MyPlanet planet, IMyCubeGrid grid)
        {
            Vector3D center = planet.WorldMatrix.Translation;
            double mid = (grid.GetPosition() - center).Length();
            double extent = 40.0;
            if (grid.PositionComp != null)
            {
                BoundingBoxD box = grid.PositionComp.WorldAABB;
                extent = (box.Max - box.Min).Length() * 0.5;
            }

            double inner = BandInnerRadius(planet);
            double outer = BandOuterRadius(planet);
            return mid + extent >= inner && mid - extent <= outer;
        }

        bool TryAimPoint(long entityId, Vector3D from, out Vector3D aim)
        {
            aim = from;
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(entityId, out entity))
                return false;

            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid == null || grid.MarkedForClose || !IsPowered(grid) || IsDamageBlocked(grid.GetPosition()))
                return false;

            if (grid.PositionComp == null)
            {
                aim = grid.GetPosition();
                return true;
            }

            aim = ClosestPoint(grid.PositionComp.WorldAABB, from);
            return true;
        }

        static Vector3D ClosestPoint(BoundingBoxD box, Vector3D point)
        {
            return new Vector3D(
                Clamp(point.X, box.Min.X, box.Max.X),
                Clamp(point.Y, box.Min.Y, box.Max.Y),
                Clamp(point.Z, box.Min.Z, box.Max.Z));
        }

        static double ApproachTarget(Wisp wisp, Vector3D heading, Vector3D radial, Vector3D center)
        {
            const double slow = 32.0;
            const double cruise = 100.0;
            const double fast = 130.0;
            const double accel = 55.0;
            double desired = cruise;
            double turn = 1.15;

            if (wisp.HasTarget)
            {
                Vector3D to = wisp.Target - wisp.Position;
                to -= radial * Vector3D.Dot(to, radial);
                double dist = to.Length();
                double align = 1.0;
                if (dist > 1.0)
                {
                    align = Vector3D.Dot(heading, to / dist);
                    if (align > 1.0)
                        align = 1.0;
                    if (align < -1.0)
                        align = -1.0;
                }

                if (dist < 180.0)
                    turn = 2.4;

                if (align > 0.92 && dist > 25.0)
                    desired = fast;
                else
                    desired = Clamp(Math.Max(16.0, dist * (1.0 - align) * 0.5) * turn, slow, cruise);

                double vertical = Math.Abs((wisp.Target - center).Length() - wisp.Altitude);
                if (vertical > 35.0 && dist < 250.0)
                    desired = Math.Min(desired, 45.0);
            }

            double step = accel * TickDt;
            if (wisp.Speed < desired)
                wisp.Speed = Math.Min(desired, wisp.Speed + step);
            else
                wisp.Speed = Math.Max(desired, wisp.Speed - step);

            return turn;
        }

        static void SteerToward(ref Vector3D heading, Vector3D desired, Vector3D radial, double maxTurn)
        {
            desired -= radial * Vector3D.Dot(desired, radial);
            if (desired.LengthSquared() < 1.0 || maxTurn <= 0.0)
                return;

            desired = Vector3D.Normalize(desired);
            double dot = Vector3D.Dot(heading, desired);
            if (dot > 1.0)
                dot = 1.0;
            if (dot < -1.0)
                dot = -1.0;

            double angle = Math.Acos(dot);
            if (angle < 0.001)
            {
                heading = desired;
                return;
            }

            double step = angle < maxTurn ? angle : maxTurn;
            if (Vector3D.Dot(Vector3D.Cross(heading, desired), radial) < 0.0)
                step = -step;

            Vector3D bitangent = Vector3D.Normalize(Vector3D.Cross(radial, heading));
            heading = Vector3D.Normalize(heading * Math.Cos(step) + bitangent * Math.Sin(step));
        }

        static void UpdateAltitude(Wisp wisp, MyPlanet planet, Vector3D center)
        {
            double minAlt = wisp.InStorm ? planet.MinimumRadius : BandInnerRadius(planet);
            double maxAlt = BandOuterRadius(planet);
            if (wisp.InStorm && wisp.HasTarget)
            {
                double targetAlt = (wisp.Target - center).Length();
                if (targetAlt > maxAlt)
                    maxAlt = targetAlt;
            }

            if (wisp.HasTarget)
            {
                double targetAlt = Clamp((wisp.Target - center).Length(), minAlt, maxAlt);
                double step = AltitudeChaseSpeed * TickDt;
                if (wisp.Altitude < targetAlt)
                    wisp.Altitude = Math.Min(wisp.Altitude + step, targetAlt);
                else
                    wisp.Altitude = Math.Max(wisp.Altitude - step, targetAlt);
            }
            else if (!wisp.InStorm && wisp.Altitude < BandInnerRadius(planet))
            {
                wisp.Altitude = Math.Min(BandInnerRadius(planet), wisp.Altitude + AltitudeChaseSpeed * TickDt);
            }

            wisp.Altitude = Clamp(wisp.Altitude, minAlt, maxAlt);
        }

        static void AvoidTerrain(MyPlanet planet, Vector3D center, Vector3D radial, ref Vector3D heading, Wisp wisp)
        {
            double look = wisp.Speed * 1.35;
            if (look < 90.0)
                look = 90.0;

            Vector3D near = SampleAhead(center, wisp.Position, heading, look * 0.45, wisp.Altitude);
            Vector3D far = SampleAhead(center, wisp.Position, heading, look, wisp.Altitude);
            double nearHeight = planet.GetHeightFromSurface(near);
            double farHeight = planet.GetHeightFromSurface(far);
            double lowest = nearHeight < farHeight ? nearHeight : farHeight;

            if (lowest < 180.0)
            {
                double rate = lowest < TerrainClearance ? 140.0 : 75.0;
                wisp.Altitude += rate * TickDt;
                if (!wisp.InStorm && wisp.Altitude > BandOuterRadius(planet))
                    wisp.Altitude = BandOuterRadius(planet);
                if (wisp.Speed > 40.0)
                    wisp.Speed -= 50.0 * TickDt;
            }

            if (lowest < 100.0)
            {
                Vector3D leftDir = Vector3D.Cross(radial, heading);
                if (leftDir.LengthSquared() < 0.01)
                    return;
                leftDir = Vector3D.Normalize(leftDir);
                double leftHeight = planet.GetHeightFromSurface(SampleAhead(center, wisp.Position, leftDir, 80.0, wisp.Altitude));
                double rightHeight = planet.GetHeightFromSurface(SampleAhead(center, wisp.Position, -leftDir, 80.0, wisp.Altitude));
                double best = leftHeight >= rightHeight ? leftHeight : rightHeight;
                if (best > lowest + 20.0)
                    SteerToward(ref heading, leftHeight >= rightHeight ? leftDir : -leftDir, radial, 1.8 * TickDt);
            }
        }

        static Vector3D SampleAhead(Vector3D center, Vector3D position, Vector3D direction, double distance, double altitude)
        {
            Vector3D point = position + direction * distance;
            Vector3D offset = point - center;
            if (offset.LengthSquared() < 1.0)
                return position;
            return center + Vector3D.Normalize(offset) * altitude;
        }

        static void KeepAboveTerrain(MyPlanet planet, Vector3D center, ref Vector3D position, ref double altitude)
        {
            double height = planet.GetHeightFromSurface(position);
            if (height >= TerrainClearance)
                return;

            altitude += TerrainClearance - height;
            Vector3D radial = position - center;
            if (radial.LengthSquared() < 1.0)
                radial = new Vector3D(0, 1, 0);
            position = center + Vector3D.Normalize(radial) * altitude;
        }

        static double BandInnerRadius(MyPlanet planet)
        {
            return planet.AverageRadius + planet.AtmosphereAltitude * BandInner;
        }

        static double BandOuterRadius(MyPlanet planet)
        {
            return planet.AverageRadius + planet.AtmosphereAltitude * BandOuter;
        }

        static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        sealed class Wisp
        {
            public int Id;
            public MyPlanet Planet;
            public Vector3D Position;
            public double Altitude;
            public Vector3D Heading;
            public Vector3D Velocity;
            public double Speed;
            public double TurnRate;
            public bool HasTarget;
            public long TargetId;
            public Vector3D Target;
            public bool InStorm;
            public int StormGrace;
            public int NextTargetTick;
            public double WanderSpeed;
            public double WanderAltitude;
            public int NextWanderTick;
            public bool WasChasing;
        }

        sealed class RemoteWisp
        {
            public Vector3D Origin;
            public Vector3D Velocity;
            public int AgeTicks;
        }

        sealed class FleetingEffect
        {
            public MyParticleEffect Effect;
            public int TicksLeft;
        }
    }

    [ProtoContract]
    public class WispPacket
    {
        [ProtoMember(1)]
        public List<WispNetState> Wisps = new List<WispNetState>();

        [ProtoMember(2)]
        public List<WispNetState> Splashes = new List<WispNetState>();
    }

    [ProtoContract]
    public class WispNetState
    {
        [ProtoMember(1)] public int Id;
        [ProtoMember(2)] public double X;
        [ProtoMember(3)] public double Y;
        [ProtoMember(4)] public double Z;
        [ProtoMember(5)] public double VX;
        [ProtoMember(6)] public double VY;
        [ProtoMember(7)] public double VZ;
    }
}
