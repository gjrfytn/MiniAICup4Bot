﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace MiniAICup4Bot
{
    internal class Program
    {
        private static void Main()
        {
#if DEBUG
            System.Threading.Thread.Sleep(5000);
#endif

            var configuration = new GameConfiguration(System.Console.ReadLine());

            var bot = new Bot(configuration);

            while (true)
            {
                var tickString = System.Console.ReadLine();

                if (tickString.Contains("end_game"))
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
        private readonly int?[,] _Field;

        private TickData _TickData;
        private int _PreviosTickNum = -6;

        public Bot(GameConfiguration configuration)
        {
            _Configuration = configuration;

            _Field = new int?[_Configuration.XCellsCount * _Configuration.CellSize, _Configuration.YCellsCount * _Configuration.CellSize];
        }

        public Action MakeTurn(TickData tickData)
        {
            _TickData = tickData;

            var dtick = (uint)(tickData.TickNum - _PreviosTickNum);
            _PreviosTickNum = (int)tickData.TickNum;

            var evaluations = new Dictionary<Direction, int>();

            WeightField();

            foreach (var direction in System.Enum.GetValues(typeof(Direction)).Cast<Direction>())
                evaluations[direction] = EvaluateDirection(direction, dtick);

            return new Action(evaluations.OrderByDescending(e => e.Value).First().Key, "test");
        }

        private void WeightField()
        {
            const int otherPlayerTerritoryWeight = 5;
            const int otherPlayerWeight = -5;

            DiscardField();

            var thisLineLength = _TickData.ThisPlayer.Lines.Count();
            foreach (var territoryPoint in _TickData.ThisPlayer.Territory)
                _Field[territoryPoint.X, territoryPoint.Y] = thisLineLength;

            foreach (var player in _TickData.OtherPlayers)
                foreach (var territoryPoint in player.Territory)
                    _Field[territoryPoint.X, territoryPoint.Y] = otherPlayerTerritoryWeight;

            foreach (var player in _TickData.OtherPlayers)
                if (_Field[player.Position.X, player.Position.Y].HasValue)
                    _Field[player.Position.X, player.Position.Y] += otherPlayerWeight;
                else
                    _Field[player.Position.X, player.Position.Y] = otherPlayerWeight;

            Propagate();
        }

        private void Propagate()
        {
            var emptyPositions = new Queue<Position>();

            for (var y = 0; y < _Field.GetLength(1); ++y)
                for (var x = 0; x < _Field.GetLength(0); ++x)
                    if (!_Field[x, y].HasValue)
                        emptyPositions.Enqueue(new Position(x, y));

            while (emptyPositions.Count > 0)
            {
                var pos = emptyPositions.Peek();

                var weights = new List<int?>
                {
                    GetWeight(pos, Direction.Left),
                    GetWeight(pos, Direction.Up),
                    GetWeight(pos, Direction.Right),
                    GetWeight(pos, Direction.Down)
                };

                var filledWeights = weights.Where(w => w.HasValue).ToArray();

                if (filledWeights.Any())
                {
                    emptyPositions.Dequeue();
                    _Field[pos.X, pos.Y] = (int)Math.Round(filledWeights.Select(w => w.Value - Math.Sign(w.Value) * -1).Average());
                }
            }
        }

        private int? GetWeight(Position pos, Direction direction)
        {
            var near = pos.Move(direction, 1);

            return InField(near) ? _Field[near.X, near.Y] : null;
        }

        private int EvaluateDirection(Direction direction, uint dtick)
        {
            const int noGoEval = -100;
            const int otherLineEval = 75;
            const int goodBonusEval = 50;
            const int badBonusEval = -50;

            var newPos = _TickData.ThisPlayer.Position.Move(direction, _Configuration.Speed * dtick);

            if (!InField(newPos))
                return noGoEval;

            if (OnPlayerLine(newPos))
                return noGoEval;

            var eval = _Field[newPos.X, newPos.Y].Value;

            foreach (var player in _TickData.OtherPlayers)
                if (player.Lines.Any(lp => lp == newPos))
                {
                    eval += otherLineEval;
                    break;
                }

            var bonusesAtNewPos = _TickData.Bonuses.Where(b => b.position == newPos).ToArray();
            if (bonusesAtNewPos.Any())
                if (bonusesAtNewPos.Any(b => b.type == BonusType.Nitro || b.type == BonusType.Saw))
                    eval += goodBonusEval;
                else
                    eval -= badBonusEval;

            return eval;
        }

        private bool OnPlayerLine(Position newPos)
        {
            foreach (var linePoint in _TickData.ThisPlayer.Lines)
                if (linePoint == newPos)
                    return true;

            return false;
        }
        private bool InField(Position newPos) => newPos.X >= 0 && newPos.Y >= 0 && newPos.X <= _Field.GetLength(0) && newPos.Y <= _Field.GetLength(1);

        private void DiscardField()
        {
            for (var y = 0; y < _Field.GetLength(1); ++y)
                for (var x = 0; x < _Field.GetLength(0); ++x)
                    _Field[x, y] = null;
        }
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

        public override bool Equals(object obj) => Equals(obj as Position);
        public bool Equals(Position other) => other != null && X == other.X && Y == other.Y;

        public override int GetHashCode()
        {
            var hashCode = 1861411795;
            hashCode = hashCode * -1521134295 + X.GetHashCode();
            hashCode = hashCode * -1521134295 + Y.GetHashCode();
            return hashCode;
        }

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
}