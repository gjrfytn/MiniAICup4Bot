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
        private readonly int[,] _Field;

        private TickData _TickData;
        private Direction _CurrentDirection;
        private int _PreviousTickNum = -6;

        public Bot(GameConfiguration configuration)
        {
            _Configuration = configuration;

            _Field = new int[_Configuration.XCellsCount, _Configuration.YCellsCount];
        }

        public Action MakeTurn(TickData tickData)
        {
            _TickData = tickData;

            var dtick = (uint)(tickData.TickNum - _PreviousTickNum);
            _PreviousTickNum = (int)tickData.TickNum;

            var evaluations = new Dictionary<Direction, int>();

            WeightField();

            foreach (var direction in System.Enum.GetValues(typeof(Direction)).Cast<Direction>())
                evaluations[direction] = EvaluateDirection(direction, dtick);

            var directionToGo = evaluations.OrderByDescending(e => e.Value).First().Key;

            _CurrentDirection = directionToGo;

            return new Action(directionToGo, "test");
        }

        private void WeightField()
        {
            DiscardField();

            foreach (var linePoint in _TickData.ThisPlayer.Lines)
                SetField(ToElementaryCellPos(linePoint), -101); //TODO

            var thisLineLength = _TickData.ThisPlayer.Lines.Count();
            var distanceToTerritory = _TickData.ThisPlayer.Territory.Min(t => System.Math.Abs(_TickData.ThisPlayer.Position.X - t.X) +
                                                                              System.Math.Abs(_TickData.ThisPlayer.Position.Y - t.Y));
            foreach (var territoryPoint in _TickData.ThisPlayer.Territory)
                Radiate(ToElementaryCellPos(territoryPoint), thisLineLength / 2 + distanceToTerritory / 2);

            foreach (var player in _TickData.OtherPlayers)
                foreach (var territoryPoint in player.Territory)
                    Radiate(ToElementaryCellPos(territoryPoint), 2);

            foreach (var player in _TickData.OtherPlayers)
                Radiate(ToElementaryCellPos(player.Position), -15);

            foreach (var player in _TickData.OtherPlayers)
                foreach (var linePoint in player.Lines)
                    Radiate(ToElementaryCellPos(linePoint), 5);

            foreach (var bonus in _TickData.Bonuses.Where(b => b.type == BonusType.Nitro || b.type == BonusType.Saw))
                Radiate(ToElementaryCellPos(bonus.position), 10);

            foreach (var bonus in _TickData.Bonuses.Where(b => b.type == BonusType.Slow))
            {
                var pos = ToElementaryCellPos(bonus.position);
                _Field[pos.X, pos.Y] -= 50;
            }

#if DEBUG
            WriteFieldDebug();
#endif
        }

        private void Radiate(Position pos, int weight)
        {
            var processedCells = new HashSet<Position>();
            var queue = new Queue<(Position pos, int weight)>();

            queue.Enqueue((pos, weight));
            while (queue.Any())
            {
                var pair = queue.Dequeue();
                SpreadRadiation(queue, processedCells, pair.pos, pair.weight);
            }
        }

        private void SpreadRadiation(Queue<(Position pos, int weight)> queue, HashSet<Position> processedCells, Position pos, int weight)
        {
            if (!processedCells.Contains(pos) && !queue.Contains((pos, weight)))
            {
                processedCells.Add(pos);

                if (pos.X >= 0 &&
                    pos.Y >= 0 &&
                    pos.X < _Field.GetLength(0) &&
                    pos.Y < _Field.GetLength(1) &&
                    _Field[pos.X, pos.Y] != -101)
                {
                    _Field[pos.X, pos.Y] += weight;

                    var change = System.Math.Sign(weight);

                    if (change != 0)
                    {
                        queue.Enqueue((pos.Move(Direction.Left, 1), weight - change));
                        queue.Enqueue((pos.Move(Direction.Up, 1), weight - change));
                        queue.Enqueue((pos.Move(Direction.Right, 1), weight - change));
                        queue.Enqueue((pos.Move(Direction.Down, 1), weight - change));
                    }
                }
            }
        }

#if DEBUG
        private void WriteFieldDebug()
        {
            using (var f = System.IO.File.CreateText("after_prop.txt"))
            {
                for (var y = _Field.GetLength(1) - 1; y >= 0; --y)
                {
                    f.WriteLine();
                    for (var x = 0; x < _Field.GetLength(0); ++x)
                        f.Write($"{_Field[x, y],-5}");
                }
            }

            using (var bmp = new System.Drawing.Bitmap(_Field.GetLength(0), _Field.GetLength(1)))
            {
                var min = 0;
                var max = 0;
                for (var y = 0; y < _Field.GetLength(1); ++y)
                    for (var x = 0; x < _Field.GetLength(0); ++x)
                    {
                        min = System.Math.Min(_Field[x, y], min);
                        max = System.Math.Max(_Field[x, y], max);
                    }

                float range = -min + max;
                for (var y = 0; y < _Field.GetLength(1); ++y)
                    for (var x = 0; x < _Field.GetLength(0); ++x)
                    {
                        var intensity = range == 0 ? 0 : (int)((_Field[x, y] - min) / range * 255);
                        bmp.SetPixel(x, _Field.GetLength(1) - y - 1, System.Drawing.Color.FromArgb(intensity, intensity, intensity));
                    }

                bmp.Save("after_prop.bmp");
            }
        }
#endif

        private int EvaluateDirection(Direction direction, uint dtick)
        {
            const int noGoEval = int.MinValue;

            if (direction == GetOppositeDirection(_CurrentDirection))
                return noGoEval;

            var newPos = _TickData.ThisPlayer.Position.Move(direction, _Configuration.Speed * dtick);

            if (newPos.X < 0 ||
                newPos.Y < 0 ||
                newPos.X >= _Configuration.CellSize * _Configuration.XCellsCount ||
                newPos.Y >= _Configuration.CellSize * _Configuration.YCellsCount)
                return noGoEval;

            return GetField(ToElementaryCellPos(newPos));
        }

        private Direction GetOppositeDirection(Direction direction)
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

        private void DiscardField()
        {
            for (var y = 0; y < _Field.GetLength(1); ++y)
                for (var x = 0; x < _Field.GetLength(0); ++x)
                    _Field[x, y] = 0;
        }

        private int GetField(Position pos) => _Field[pos.X, pos.Y];
        private void SetField(Position pos, int value) => _Field[pos.X, pos.Y] = value;
        private Position ToElementaryCellPos(Position pos) => new Position((int)(pos.X / _Configuration.CellSize), (int)(pos.Y / _Configuration.CellSize));
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
}
