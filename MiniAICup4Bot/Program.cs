﻿using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace MiniAICup4Bot
{
    internal class Program
    {
        private static void Main()
        {
#if DEBUG
            System.Threading.Thread.Sleep(3000);
#endif

            var configuration = new GameConfiguration(System.Console.ReadLine());

            var bot = new Bot(configuration);

            while (true)
            {
                var tickString = System.Console.ReadLine();

                if (tickString == null || tickString.Contains("end_game"))
                    break;

                var tickData = new TickData(tickString);
                var action = bot.MakeTurn(tickData);

                System.Console.WriteLine(action.ToJson());
            }
        }

        public static BonusType ParseBonusType(string typeString)
        {
            switch (typeString)
            {
                case "n": return BonusType.Nitro;
                case "s": return BonusType.Slow;
                case "saw": return BonusType.Saw;
                default: throw new System.ArgumentOutOfRangeException(nameof(typeString));
            }
        }
    }

    internal class Bot
    {
        private readonly GameConfiguration _Configuration;

        private TickData _TickData;
        private Direction _CurrentDirection;
        private int _PreviousTickNum = -6;
        private IEnumerable<Direction> _AvailableDirections;
        private Position _PlayerPos;
        private string _DebugMessage;
        private (Position pos, uint distance)? _ClosestTerritory;
        private (Position pos, uint distance)? _FurthestTerritory;

#if DEBUG
        private readonly System.IO.StreamWriter _LogStream = System.IO.File.CreateText("debug.log");
#endif

        private (Position pos, uint distance) ClosestTerritory
        {
            get
            {
                if (!_ClosestTerritory.HasValue)
                {
                    Position closestTerritory = null;
                    var distanceToTerritory = uint.MaxValue;
                    foreach (var territoryPoint in _TickData.ThisPlayer.Territory.Select(t => ToElementaryCellPos(t)))
                    {
                        var dist = Distance(_PlayerPos, territoryPoint);
                        if (dist < distanceToTerritory)
                        {
                            closestTerritory = territoryPoint;
                            distanceToTerritory = dist;
                        }
                    }

                    _ClosestTerritory = (closestTerritory, distanceToTerritory);
                }

                return _ClosestTerritory.Value;
            }
        }

        private (Position pos, uint distance) FurthestTerritory
        {
            get
            {
                if (!_FurthestTerritory.HasValue)
                {
                    Position furthestTerritory = null;
                    var distanceToTerritory = uint.MinValue;
                    foreach (var territoryPoint in _TickData.ThisPlayer.Territory.Select(t => ToElementaryCellPos(t)))
                    {
                        var dist = Distance(_PlayerPos, territoryPoint);
                        if (dist > distanceToTerritory)
                        {
                            furthestTerritory = territoryPoint;
                            distanceToTerritory = dist;
                        }
                    }

                    _FurthestTerritory = (furthestTerritory, distanceToTerritory);
                }

                return _FurthestTerritory.Value;
            }
        }

        public Bot(GameConfiguration configuration)
        {
            _Configuration = configuration;
        }

        public Action MakeTurn(TickData tickData)
        {
            _TickData = tickData;

            _ClosestTerritory = null;

            var dtick = (uint)(tickData.TickNum - _PreviousTickNum);
            _PreviousTickNum = (int)tickData.TickNum;

            _AvailableDirections = GetAvailableDirections(_TickData.ThisPlayer.Position, _CurrentDirection, dtick);
            _PlayerPos = ToElementaryCellPos(_TickData.ThisPlayer.Position);

            var directionToGo = GetDirection();

            _CurrentDirection = directionToGo;

            return new Action(directionToGo, _DebugMessage);
        }

        private IEnumerable<Direction> GetAvailableDirections(Position pos, Direction currentDirection, uint dtick)
        {
            var possibleDirections = new List<Direction>();
            foreach (var direction in System.Enum.GetValues(typeof(Direction)).Cast<Direction>())
            {
                var newPos = pos.Move(direction, _Configuration.Speed * dtick);

                if (direction == OppositeDirection(currentDirection))
                    continue;

                if (newPos.X < 0 ||
                    newPos.Y < 0 ||
                    newPos.X >= _Configuration.CellSize * _Configuration.XCellsCount ||
                    newPos.Y >= _Configuration.CellSize * _Configuration.YCellsCount)
                    continue;

                if (_TickData.ThisPlayer.Lines.Any(lp => lp == newPos))
                    continue;

                possibleDirections.Add(direction);
            }

            //TODO Bad bonus avoidance.

            return possibleDirections;
        }

        private Direction GetDirection()
        {
            Log($"Deciding where to go, I have this possibilities: {string.Join(", ", _AvailableDirections)}.");

            if (!_AvailableDirections.Any())
            {
                Log("I DONT KNOW WHAT TO DO!");

                return Direction.Left;
            }

            var direction = Flee();

            if (direction.HasValue)
            {
                Log("Fleeing!");

                return direction.Value;
            }

            direction = Attack();

            if (direction.HasValue)
            {
                Log("Attacking!");

                return direction.Value;
            }

            direction = PickUpBonus();

            if (direction.HasValue)
            {
                Log("Going for bonus.");

                return direction.Value;
            }

            direction = CaptureTerritory();

            if (direction.HasValue)
            {
                Log("Capturing territory.");

                return direction.Value;
            }

            Log("I DONT KNOW WHAT TO DO!");

            return _AvailableDirections.First();
        }

        private Direction? CaptureTerritory()
        {
            var lineLength = _TickData.ThisPlayer.Lines.Count();
            if (lineLength <= 10)
            {
                var mirroredPos = new Position(2 * _PlayerPos.X - ClosestTerritory.pos.X, 2 * _PlayerPos.Y - ClosestTerritory.pos.Y);

                if (mirroredPos == _PlayerPos)
                    return GoTo(_TickData.OtherPlayers.First().Territory.First());

                return GoTo(mirroredPos);
            }
            else
            {
                return GoTo(FurthestTerritory.pos);
            }
        }

        private Direction? Attack()
        {
            const int attackRange = 3;

            foreach (var player in _TickData.OtherPlayers)
                foreach (var linePoint in player.Lines.Select(lp => ToElementaryCellPos(lp)))
                    if (Distance(_PlayerPos, linePoint) <= attackRange)
                        return GoTo(linePoint);

            return null;
        }

        private Direction? PickUpBonus()
        {
            const int pickUpRange = 3;

            foreach (var bonus in _TickData.Bonuses.Where(b => b.type == BonusType.Nitro || b.type == BonusType.Saw).Select(b => ToElementaryCellPos(b.position)))
                if (Distance(_PlayerPos, bonus) <= pickUpRange)
                    return GoTo(bonus);

            return null;
        }

        private void Log(string message)
        {
            _DebugMessage = message;
#if DEBUG
            _LogStream.WriteLine(message);
            _LogStream.Flush();
#endif
        }

        private Direction? Flee()
        {
            const int reassuranceDist = 3;

            if (ClosestTerritory.distance != 0)
                foreach (var linePoint in _TickData.ThisPlayer.Lines.Select(lp => ToElementaryCellPos(lp)))
                    foreach (var player in _TickData.OtherPlayers)
                        if (Distance(linePoint, ToElementaryCellPos(player.Position)) <= ClosestTerritory.distance + reassuranceDist)
                            return GoTo(ClosestTerritory.pos);

            return null;
        }

        private Direction GoTo(Position pos)
        {
            var processedCells = new Dictionary<Position, Direction/*float weight*/>();
            var queue = new Queue<Position>();//new SortedSet<(Position pos,/* Direction direction,*/ /*float weight*/)>(new PathfindingComparer());

            processedCells.Add(_PlayerPos, _CurrentDirection);
            queue.Enqueue(_PlayerPos);//Add((_PlayerPos, /*_CurrentDirection,*/0));
            while (queue.Any())
            {
                var node = queue.Dequeue();
                if (Spread(queue, processedCells, node, pos))
                {
                    var current = pos;
                    var path = new List<Position>();
                    while (current != _PlayerPos)
                    {
                        path.Add(current);
                        current = current.Move(OppositeDirection(processedCells[current]));
                    }

                    return processedCells[path.Last()];
                }
            }

            return _AvailableDirections.First();
        }

        private bool Spread(Queue<Position> queue, Dictionary<Position, Direction> processedCells, Position pos, Position target)
        {
            var cameFrom = processedCells[pos];

            if (pos == target)
                return true;

            foreach (var direction in GetAvailableDirections(ToFractionedCellPos(pos), cameFrom, 6)) //TODO
            {
                var newPos = pos.Move(direction);
                if (!processedCells.ContainsKey(newPos))
                {
                    processedCells.Add(newPos, direction);
                    queue.Enqueue(newPos);
                }
            }

            return false;
        }

        private uint Distance(Position p1, Position p2) => (uint)(System.Math.Abs(p1.X - p2.X) + System.Math.Abs(p1.Y - p2.Y));

        private Direction OppositeDirection(Direction direction)
        {
            switch (direction)
            {
                case Direction.Left: return Direction.Right;
                case Direction.Up: return Direction.Down;
                case Direction.Right: return Direction.Left;
                case Direction.Down: return Direction.Up;
                default: throw new System.ArgumentOutOfRangeException(nameof(direction));
            }
        }

        private Position ToElementaryCellPos(Position pos) => new Position((int)(pos.X / _Configuration.CellSize), (int)(pos.Y / _Configuration.CellSize));
        private Position ToFractionedCellPos(Position pos) => new Position((int)(_Configuration.CellSize * pos.X), (int)(_Configuration.CellSize * pos.Y));
    }

    internal class GameConfiguration
    {
        public uint XCellsCount { get; }
        public uint YCellsCount { get; }
        public uint Speed { get; }
        public uint CellSize { get; }

        public GameConfiguration(string initString)
        {
            var root = Newtonsoft.Json.JsonConvert.DeserializeXNode(initString, "root").Root.Element("params");
            XCellsCount = uint.Parse(root.Element("x_cells_count").Value);
            YCellsCount = uint.Parse(root.Element("y_cells_count").Value);
            Speed = uint.Parse(root.Element("speed").Value);
            CellSize = uint.Parse(root.Element("width").Value);
        }
    }

    internal enum Direction
    {
        Left,
        Up,
        Right,
        Down
    }

    internal enum BonusType
    {
        Nitro,
        Slow,
        Saw
    }

    internal class TickData
    {
        private readonly Dictionary<string, PlayerState> _Players = new Dictionary<string, PlayerState>();
        private readonly List<(BonusType type, Position position)> _Bonuses = new List<(BonusType, Position)>();

        public IReadOnlyDictionary<string, PlayerState> Players => _Players;
        public IEnumerable<(BonusType type, Position position)> Bonuses => _Bonuses;
        public uint TickNum { get; }

        public TickData(string tickString)
        {
            var root = Newtonsoft.Json.JsonConvert.DeserializeXNode(tickString, "root").Root.Element("params");

            foreach (var playerElement in root.Element("players").Elements())
                _Players.Add(playerElement.Name.ToString(), new PlayerState(playerElement));

            foreach (var bonusElement in root.Elements("bonuses"))
            {
                var coords = bonusElement.Elements("position").ToArray();
                _Bonuses.Add((Program.ParseBonusType(bonusElement.Element("type").Value),
                              new Position(int.Parse(coords[0].Value), int.Parse(coords[1].Value))));
            }

            TickNum = uint.Parse(root.Element("tick_num").Value);
        }

        public PlayerState ThisPlayer => _Players["i"];
        public IEnumerable<PlayerState> OtherPlayers => _Players.Where(p => p.Key != "i").Select(p => p.Value);
    }

    internal class PlayerState
    {
        private readonly List<(BonusType type, uint ticks)> _Bonuses = new List<(BonusType, uint)>();
        private readonly List<Position> _Territory = new List<Position>();
        private readonly List<Position> _Lines = new List<Position>();

        public uint Score { get; }
        public IEnumerable<Position> Territory => _Territory;
        public Position Position { get; }
        public IEnumerable<Position> Lines => _Lines;
        public Direction? Direction { get; }
        public IEnumerable<(BonusType type, uint ticks)> Bonuses => _Bonuses;

        public PlayerState(XElement element)
        {
            Score = uint.Parse(element.Element("score").Value);

            foreach (var territoryElement in element.Elements("territory"))
            {
                var coords = territoryElement.Elements().ToArray();
                _Territory.Add(new Position(int.Parse(coords[0].Value), int.Parse(coords[1].Value)));
            }

            var posCoords = element.Elements("position").ToArray();
            Position = new Position(int.Parse(posCoords[0].Value), int.Parse(posCoords[1].Value));

            foreach (var lineElement in element.Elements("lines"))
            {
                var coords = lineElement.Elements().ToArray();
                _Lines.Add(new Position(int.Parse(coords[0].Value), int.Parse(coords[1].Value)));
            }

            Direction = ParseDirection(element.Element("direction").Value);

            foreach (var bonusElement in element.Elements("bonuses"))
                _Bonuses.Add((Program.ParseBonusType(bonusElement.Element("type").Value), uint.Parse(bonusElement.Element("ticks").Value)));
        }

        private static Direction? ParseDirection(string directionString)
        {
            switch (directionString)
            {
                case "": return null;
                case "left": return MiniAICup4Bot.Direction.Left;
                case "up": return MiniAICup4Bot.Direction.Up;
                case "right": return MiniAICup4Bot.Direction.Right;
                case "down": return MiniAICup4Bot.Direction.Down;
                default: throw new System.ArgumentOutOfRangeException(nameof(directionString));
            }
        }
    }

    internal class Action
    {
        private readonly Direction _MoveDirection;
        private readonly string _DebugMessage;

        public Action(Direction moveDirection, string debugMessage)
        {
            _MoveDirection = moveDirection;
            _DebugMessage = debugMessage;
        }

        public string ToJson() => $"{{\"command\": \"{DirectionToString(_MoveDirection)}\", \"debug\": \"{_DebugMessage}\"}}";

        private static string DirectionToString(Direction direction)
        {
            switch (direction)
            {
                case Direction.Left: return "left";
                case Direction.Up: return "up";
                case Direction.Right: return "right";
                case Direction.Down: return "down";
                default: throw new System.ArgumentOutOfRangeException(nameof(direction));
            }
        }
    }

    internal class Position : System.IEquatable<Position>
    {
        public int X { get; }
        public int Y { get; }

        public Position(int x, int y)
        {
            X = x;
            Y = y;
        }

        public Position Move(Direction direction, uint speed)
        {
            switch (direction)
            {
                case Direction.Left: return new Position((int)(X - speed), Y);
                case Direction.Up: return new Position(X, (int)(Y + speed));
                case Direction.Right: return new Position((int)(X + speed), Y);
                case Direction.Down: return new Position(X, (int)(Y - speed));
                default: throw new System.ArgumentOutOfRangeException(nameof(direction));
            }
        }

        public Position Move(Direction direction) => Move(direction, 1);

        public override bool Equals(object obj) => Equals(obj as Position);
        public bool Equals(Position other) => other != null && X == other.X && Y == other.Y;

        public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode();

        public override string ToString() => $"{{{X}; {Y}}}";

        public static bool operator ==(Position left, Position right)
        {
            return EqualityComparer<Position>.Default.Equals(left, right);
        }

        public static bool operator !=(Position left, Position right)
        {
            return !(left == right);
        }
    }

    internal class PathfindingComparer : IComparer<(Position pos, Direction dir, float length)>
    {
        public int Compare((Position pos, Direction dir, float length) x, (Position pos, Direction dir, float length) y) => System.Math.Sign(x.length - y.length);
    }
}
