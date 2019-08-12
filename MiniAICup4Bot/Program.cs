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
        private const int _PredictorDistance = 4;

        private readonly GameConfiguration _Configuration;

        private TickData _TickData;
        private IEnumerable<Direction> _AvailableDirections;
        private IEnumerable<Direction> _SafeDirections;
        private Position _PlayerPos;
        private string _DebugMessage;
        private (Position pos, uint distance)? _ClosestTerritory;
        private (Position pos, uint distance)? _FurthestTerritory;
        private GameState _GameState;
        private Helper _Helper;
        private Navigator _Navigator;

#if DEBUG
        private readonly System.IO.StreamWriter _LogStream = System.IO.File.CreateText("debug.log");
#endif

        private Direction CurrentDirection => _TickData.ThisPlayer.Direction ?? Direction.Left;

        private (Position pos, uint distance) ClosestTerritory
        {
            get
            {
                if (!_ClosestTerritory.HasValue)
                {
                    Position closestTerritory = null;
                    var distanceToTerritory = uint.MaxValue;
                    foreach (var territoryPoint in _TickData.ThisPlayer.Territory.Select(t => _Helper.ToElementaryCellPos(t)))
                    {
                        var dist = Helper.Distance(_PlayerPos, territoryPoint);
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
                    foreach (var territoryPoint in _TickData.ThisPlayer.Territory.Select(t => _Helper.ToElementaryCellPos(t)))
                    {
                        var dist = Helper.Distance(_PlayerPos, territoryPoint);
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

        private GameState GameState
        {
            get
            {
                if (_GameState == null)
                {
                    var players = _TickData.Players.Where(p => p.Value == _TickData.ThisPlayer ||
                                                               Helper.Distance(_Helper.ToElementaryCellPos(_TickData.ThisPlayer.Position), _Helper.ToElementaryCellPos(p.Value.Position)) <= _PredictorDistance ||
                                                               _TickData.ThisPlayer.Lines.Any(lp => Helper.Distance(_Helper.ToElementaryCellPos(lp), _Helper.ToElementaryCellPos(p.Value.Position)) <= _PredictorDistance))
                                                   .Select(p => new Player(p.Key,
                                                                           _Helper.ToElementaryCellPos(p.Value.Position),
                                                                           p.Value.Direction ?? Direction.Left,//TODO
                                                                           p.Value.Territory.Select(t => _Helper.ToElementaryCellPos(t)),
                                                                           p.Value.Lines.Select(lp => _Helper.ToElementaryCellPos(lp)),
                                                                           p.Value == _TickData.ThisPlayer));

                    _GameState = new GameState(_Configuration, players);
                }

                return _GameState;
            }
        }

        public Bot(GameConfiguration configuration)
        {
            _Configuration = configuration;
        }

        public Action MakeTurn(TickData tickData)
        {
            _TickData = tickData;

            _Helper = new Helper(_Configuration);

            _GameState = null;
            _ClosestTerritory = null;
            _FurthestTerritory = null;

            _AvailableDirections = _Helper.GetAvailableDirections(_Helper.ToElementaryCellPos(_TickData.ThisPlayer.Position),
                                                                  CurrentDirection,
                                                                  _TickData.ThisPlayer.Lines.Select(lp => _Helper.ToElementaryCellPos(lp)));
            _PlayerPos = _Helper.ToElementaryCellPos(_TickData.ThisPlayer.Position);

            _Navigator = new Navigator(_TickData, _Helper, _PlayerPos, CurrentDirection);

            var directionToGo = GetDirection();

#if DEBUG
            Log(directionToGo.ToString());
#endif

            return new Action(directionToGo, _DebugMessage);
        }

        private Direction GetDirection()
        {
            Log($"Deciding where to go, I have this possibilities: {string.Join(", ", _AvailableDirections)}.");

            if (!_AvailableDirections.Any())
            {
                Log("I DONT KNOW WHAT TO DO!");

                return Direction.Left;
            }

            FindSafeDirections();

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
                if (!_TickData.ThisPlayer.Lines.Any())
                {
                    if (_TickData.OtherPlayers.Any())
                    {
                        var direction = GoTo(_Helper.ToElementaryCellPos(_TickData.OtherPlayers.First().Territory.First()));
                        var newPos = _PlayerPos.Move(direction);
                        var territory = _TickData.ThisPlayer.Territory.Select(t => _Helper.ToElementaryCellPos(t)).ToArray();

                        if (!territory.Any(t => t == newPos) && _TickData.OtherPlayers.Min(p => Helper.Distance(_Helper.ToElementaryCellPos(p.Position), newPos)) <= 3)
                        {
                            var neighbourTerritory = territory.FirstOrDefault(t => Helper.Distance(_PlayerPos, t) == 1);
                            if (neighbourTerritory != null)
                                return GoTo(neighbourTerritory);
                        }

                        return direction;
                    }

                    var rand = new System.Random();

                    return GoTo(new Position(rand.Next((int)_Configuration.XCellsCount), rand.Next((int)_Configuration.YCellsCount)));
                }

                var mirroredPos = new Position(2 * _PlayerPos.X - ClosestTerritory.pos.X, 2 * _PlayerPos.Y - ClosestTerritory.pos.Y);

                return GoTo(mirroredPos);
            }
            else if (ClosestTerritory.distance == 1)
                return GoTo(ClosestTerritory.pos);
            else
                return GoTo(FurthestTerritory.pos);
        }

        private Direction? Attack()
        {
            const int attackRange = 3;

            foreach (var player in _TickData.OtherPlayers)
            {
                var enemyPos = _Helper.ToElementaryCellPos(player.Position);
                var enemyDistanceToHisTerritory = player.Territory.Min(t => Helper.Distance(enemyPos, _Helper.ToElementaryCellPos(t)));

                Position targetLinePoint = null;
                var minDistance = uint.MaxValue;
                foreach (var linePoint in player.Lines.Select(lp => _Helper.ToElementaryCellPos(lp)))
                {
                    var dist = Helper.Distance(_PlayerPos, linePoint);

                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        if (dist <= attackRange)
                            targetLinePoint = linePoint;
                    }
                }

                if (targetLinePoint != null)
                    return GoTo(targetLinePoint);
            }

            return null;
        }

        private Direction? PickUpBonus()
        {
            const int pickUpRange = 3;

            foreach (var bonus in _TickData.Bonuses.Where(b => b.type == BonusType.Nitro || b.type == BonusType.Saw).Select(b => _Helper.ToElementaryCellPos(b.position)))
                if (Helper.Distance(_PlayerPos, bonus) <= pickUpRange)
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
            Direction GoHome()
            {
                var direction = GoTo(ClosestTerritory.pos);

                if (!_SafeDirections.Contains(direction))
                    return _SafeDirections.First(); //TODO

                return direction;
            }

            if (!_SafeDirections.Any())
                return GoTo(ClosestTerritory.pos);

            if (_SafeDirections.Count() == 1)
                return _SafeDirections.Single();

            const int reassuranceDist = 4;
            var fleeDist = ClosestTerritory.distance + reassuranceDist;

            if (ClosestTerritory.distance >= 2)
                foreach (var player in _TickData.OtherPlayers)
                {
                    var playerPos = _Helper.ToElementaryCellPos(player.Position);
                    if (Helper.Distance(ClosestTerritory.pos, playerPos) <= fleeDist)
                        return GoHome();

                    foreach (var linePoint in _TickData.ThisPlayer.Lines.Select(lp => _Helper.ToElementaryCellPos(lp)))
                    {
                        if (Helper.Distance(linePoint, playerPos) <= fleeDist)
                            return GoHome();
                    }
                }

            return null;
        }

        private void FindSafeDirections()
        {
            var dangerousDirections = GameState.Players.Count() > 1 ? Predictor.GetDeathDirections(GameState, _PredictorDistance) : Enumerable.Empty<Direction>(); //TODO 4?

            _SafeDirections = _AvailableDirections.Except(dangerousDirections);
        }

        private Direction GoTo(Position pos) => _Navigator.GoTo(pos) ?? _AvailableDirections.First();
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
        private readonly List<(BonusType type, BasicPosition position)> _Bonuses = new List<(BonusType, BasicPosition)>();

        public IReadOnlyDictionary<string, PlayerState> Players => _Players;
        public IEnumerable<(BonusType type, BasicPosition position)> Bonuses => _Bonuses;
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
                              new BasicPosition(int.Parse(coords[0].Value), int.Parse(coords[1].Value))));
            }

            TickNum = uint.Parse(root.Element("tick_num").Value);
        }

        public PlayerState ThisPlayer => _Players["i"];
        public IEnumerable<PlayerState> OtherPlayers => _Players.Where(p => p.Key != "i").Select(p => p.Value);
    }

    internal class PlayerState
    {
        private readonly List<(BonusType type, uint ticks)> _Bonuses = new List<(BonusType, uint)>();
        private readonly List<BasicPosition> _Territory = new List<BasicPosition>();
        private readonly List<BasicPosition> _Lines = new List<BasicPosition>();

        public uint Score { get; }
        public IEnumerable<BasicPosition> Territory => _Territory;
        public BasicPosition Position { get; }
        public IEnumerable<BasicPosition> Lines => _Lines;
        public Direction? Direction { get; }
        public IEnumerable<(BonusType type, uint ticks)> Bonuses => _Bonuses;

        public PlayerState(XElement element)
        {
            Score = uint.Parse(element.Element("score").Value);

            foreach (var territoryElement in element.Elements("territory"))
            {
                var coords = territoryElement.Elements().ToArray();
                _Territory.Add(new BasicPosition(int.Parse(coords[0].Value), int.Parse(coords[1].Value)));
            }

            var posCoords = element.Elements("position").ToArray();
            Position = new BasicPosition(int.Parse(posCoords[0].Value), int.Parse(posCoords[1].Value));

            foreach (var lineElement in element.Elements("lines"))
            {
                var coords = lineElement.Elements().ToArray();
                _Lines.Add(new BasicPosition(int.Parse(coords[0].Value), int.Parse(coords[1].Value)));
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

    internal class BasicPosition
    {
        public int X { get; }
        public int Y { get; }

        public BasicPosition(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    internal class Position : BasicPosition, System.IEquatable<Position>
    {
        public Position(int x, int y) : base(x, y) { }

        public Position Move(Direction direction)
        {
            switch (direction)
            {
                case Direction.Left: return new Position(X - 1, Y);
                case Direction.Up: return new Position(X, Y + 1);
                case Direction.Right: return new Position(X + 1, Y);
                case Direction.Down: return new Position(X, Y - 1);
                default: throw new System.ArgumentOutOfRangeException(nameof(direction));
            }
        }

        public override bool Equals(object obj) => Equals(obj as Position);
        public bool Equals(Position other) => other != null && X == other.X && Y == other.Y;

        public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode();

        public override string ToString() => $"{{{X}; {Y}}}";

        public static bool operator ==(Position left, Position right) => EqualityComparer<Position>.Default.Equals(left, right);

        public static bool operator !=(Position left, Position right) => !(left == right);
    }

    internal class PathfindingComparer : IComparer<(Position pos, float cost)>
    {
        public int Compare((Position pos, float cost) x, (Position pos, float cost) y) => System.Math.Sign(x.cost - y.cost);
    }

    internal class Helper
    {
        private readonly GameConfiguration _Configuration;

        public Helper(GameConfiguration configuration)
        {
            _Configuration = configuration;
        }

        public Position ToElementaryCellPos(BasicPosition pos) => new Position((int)(pos.X / _Configuration.CellSize), (int)(pos.Y / _Configuration.CellSize));

        public IEnumerable<Direction> GetAvailableDirections(Position pos, Direction currentDirection, IEnumerable<Position> playerLinePoints)
        {
            var availableDirections = new List<Direction>();
            foreach (var direction in System.Enum.GetValues(typeof(Direction)).Cast<Direction>())
            {
                var newPos = pos.Move(direction);

                if (direction == OppositeDirection(currentDirection))
                    continue;

                if (newPos.X < 0 ||
                    newPos.Y < 0 ||
                    newPos.X >= _Configuration.XCellsCount ||
                    newPos.Y >= _Configuration.YCellsCount)
                    continue;

                if (playerLinePoints.Any(lp => lp == newPos))
                    continue;

                availableDirections.Add(direction);
            }

            return availableDirections;
        }

        public static Direction OppositeDirection(Direction direction)
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

        public static uint Distance(Position p1, Position p2) => (uint)(System.Math.Abs(p1.X - p2.X) + System.Math.Abs(p1.Y - p2.Y));
    }

    internal class Navigator
    {
        private readonly TickData _TickData;
        private readonly Helper _Helper;
        private readonly Position _InitialPos;
        private readonly Direction _CurrentDirection;

        public Navigator(TickData tickData, Helper helper, Position initialPos, Direction currentDirection)
        {
            _TickData = tickData;
            _Helper = helper;
            _InitialPos = initialPos;
            _CurrentDirection = currentDirection;
        }

        public Direction? GoTo(Position pos)
        {
            var processedCells = new Dictionary<Position, Direction>();
            var costs = new Dictionary<Position, float>();
            var queue = new List<(Position pos, float cost)>();

            processedCells[_InitialPos] = _CurrentDirection;
            costs[_InitialPos] = 0;
            queue.Add((_InitialPos, 0));
            while (queue.Any())
            {
                queue.Sort(new PathfindingComparer());
                var node = queue.First();
                queue.Remove(node);
                if (Spread(queue, processedCells, costs, node, pos))
                {
                    var current = pos;
                    var path = new List<Position>();
                    var watchdog = 0;
                    while (current != _InitialPos)
                    {
                        path.Add(current);
                        current = current.Move(Helper.OppositeDirection(processedCells[current]));

                        watchdog++;
                        if (watchdog > 500)
                            break;
                    }

                    if (path.Any())
                        return processedCells[path.Last()];

                    break;
                }
            }

            return null;
        }

        private bool Spread(List<(Position pos, float cost)> queue, Dictionary<Position, Direction> processedCells, Dictionary<Position, float> costs, (Position pos, float cost) node, Position target)
        {
            var cameFrom = processedCells[node.pos];

            if (node.pos == target)
                return true;

            foreach (var direction in _Helper.GetAvailableDirections(node.pos, cameFrom, _TickData.ThisPlayer.Lines.Select(lp => _Helper.ToElementaryCellPos(lp))))
            {
                var newPos = node.pos.Move(direction);
                var newCost = node.cost + (processedCells[node.pos] == direction ? 0.99f : 1);
                if (!processedCells.ContainsKey(newPos) || newCost < costs[newPos])
                {
                    processedCells[newPos] = direction;
                    costs[newPos] = newCost;
                    queue.Add((newPos, newCost));
                }
            }

            return false;
        }
    }

    internal static class Predictor
    {
        public static IEnumerable<Direction> GetDeathDirections(GameState gameState, uint depth)
        {
            if (depth == 0)
                return Enumerable.Empty<Direction>();

            var childStates = gameState.GetChildStates();

            var deathDirections = childStates.GroupBy(s => s.playerMove)
                                             .Where(g => g.Any(s => s.state.PlayerIsDead))
                                             .Select(g => g.Key.Value)
                                             .ToList();

            foreach (var childState in childStates.Where(s => !s.state.PlayerIsDead))
                if (IsDeadInAllChildStates(childState.state, depth))
                    deathDirections.Add(childState.playerMove.Value);

            return deathDirections;

            //if (depth == 0)
            //    return Enumerable.Empty<Direction>();

            //var childStates = gameState.GetChildStates();

            //var deathDirections = childStates.GroupBy(s => s.playerMove)
            //                                 .Where(g => g.Any(s => IsDeadForState(s.state, depth - 1)))
            //                                 .Select(g => g.Key.Value)
            //                                 .ToList();

            //return deathDirections;
        }

        private static bool IsDeadInAllChildStates(GameState gameState, uint depth)
        {
            if (depth == 1)
                return false;

            return gameState.GetChildStates().All(s => s.state.PlayerIsDead || IsDeadInAllChildStates(s.state, depth - 1));
        }

        //private static bool IsDeadForState(GameState gameState, uint depth)
        //{
        //    if (depth == 0)
        //        return false;

        //    return gameState.PlayerIsDead || gameState.GetChildStates().All(s => IsDeadForState(s.state, depth - 1));
        //}
    }

    internal class GameState
    {
        private readonly GameConfiguration _Configuration;
        private readonly List<Player> _Players;

        public IEnumerable<Player> Players => _Players;
        public bool PlayerIsDead => !_Players.Any(p => p.IsControlledPlayer);

        public GameState(GameConfiguration configuration, IEnumerable<Player> players)
        {
            _Configuration = configuration;
            _Players = players.ToList();
        }

        public GameState(GameState gameState)
        {
            _Configuration = gameState._Configuration;
            _Players = gameState.Players.Select(p => new Player(p)).ToList();
        }

        public IEnumerable<(GameState state, Direction? playerMove)> GetChildStates()
        {
            var helper = new Helper(_Configuration);
            var controlledPlayerKey = Players.Single(p => p.IsControlledPlayer).Key;
            var moves = new List<(string playerKey, Direction move)>();
            foreach (var player in Players)
                foreach (var direction in helper.GetAvailableDirections(player.Pos, player.Direction, player.LinePoints))
                    moves.Add((player.Key, direction));

            var moveVariants = Combine(moves.GroupBy(m1 => m1.playerKey, m2 => m2.move, (k, g) => (k, g)));

            var childStates = new List<(GameState state, Direction? playerMove)>();
            foreach (var variant in moveVariants)
            {
                var state = new GameState(this);
                foreach (var playerMove in variant)
                    state.Players.Single(p => p.Key == playerMove.playerKey).Move(playerMove.direction);

                state.CalculateCollisions();
                Direction? controlledPlayerMove = null;
                if (variant.Any(p => p.playerKey == controlledPlayerKey))
                    controlledPlayerMove = variant.Single(p => p.playerKey == controlledPlayerKey).direction;

                childStates.Add((state, controlledPlayerMove));
            }

            return childStates;
        }

        private static IEnumerable<IEnumerable<(string playerKey, Direction direction)>> Combine(IEnumerable<(string playerKey, IEnumerable<Direction> availableDirections)> playersDirections)
        {
            var playersDirectionsArr = playersDirections.Select(pd => new { PlayerKey = pd.playerKey, Directions = pd.availableDirections.ToArray() }).ToArray();
            var combinationsCount = 1;

            foreach (var playerDirections in playersDirectionsArr)
                combinationsCount *= playerDirections.Directions.Length;

            var combinationsGrid = new Direction[playersDirectionsArr.Length, combinationsCount];
            var totalCombs = 1;
            for (var p = 0; p < playersDirectionsArr.Length; ++p)
            {
                var dirCount = playersDirectionsArr[p].Directions.Length;
                totalCombs *= dirCount;
                for (var c = 0; c < combinationsCount; ++c)
                {
                    combinationsGrid[p, c] = playersDirectionsArr[p].Directions[c / (combinationsCount / totalCombs) % dirCount];
                }
            }

            var combinations = new List<IEnumerable<(string playerKey, Direction direction)>>();

            for (var c = 0; c < combinationsCount; ++c)
            {
                var combination = new List<(string playerKey, Direction direction)>();

                for (var p = 0; p < playersDirectionsArr.Length; ++p)
                {
                    combination.Add((playersDirectionsArr[p].PlayerKey, combinationsGrid[p, c]));
                }

                combinations.Add(combination);
            }

            return combinations;
        }

        private void CalculateCollisions()
        {
            var killedPlayers = new List<Player>();
            foreach (var player in _Players)
            {
                var killed = false;
                var collidedPlayers = _Players.Where(p => p != player && p.Pos == player.Pos);
                foreach (var collidedPlayer in collidedPlayers)
                    if (player.LinePoints.Count() >= collidedPlayer.LinePoints.Count())
                    {
                        killedPlayers.Add(player);
                        killed = true;

                        break;
                    }

                if (!killed && player.LinePoints.Any(lp => _Players.Any(p => lp == p.Pos)))
                {
                    killedPlayers.Add(player);

                    continue;
                }
            }

            foreach (var player in killedPlayers)
                _Players.Remove(player);
        }
    }

    internal class Player
    {
        private readonly List<Position> _LinePoints;
        private readonly List<Position> _Territory;

        public string Key { get; }
        public Position Pos { get; private set; }
        public Direction Direction { get; private set; }
        public IEnumerable<Position> Territory => _Territory;
        public IEnumerable<Position> LinePoints => _LinePoints;
        public bool IsControlledPlayer { get; }

        public Player(string key, Position pos, Direction direction, IEnumerable<Position> territory, IEnumerable<Position> linePoints, bool isControlledPlayer)
        {
            Key = key;
            Pos = pos;
            Direction = direction;
            _Territory = territory.ToList();
            _LinePoints = linePoints.ToList();
            IsControlledPlayer = isControlledPlayer;
        }

        public Player(Player player)
        {
            Key = player.Key;
            Pos = new Position(player.Pos.X, player.Pos.Y);
            Direction = player.Direction;
            _Territory = player.Territory.ToList();
            _LinePoints = player.LinePoints.ToList();
            IsControlledPlayer = player.IsControlledPlayer;
        }

        public void Move(Direction direction)
        {
            if (Helper.OppositeDirection(Direction) == direction)
                throw new System.Exception("Cannot move in opposite direction.");

            Direction = direction;

            if (!_Territory.Contains(Pos))
                _LinePoints.Add(Pos);

            Pos = Pos.Move(direction);

            if (_Territory.Contains(Pos))
            {
                //TODO Territory addition.
                _LinePoints.Clear();
            }
        }
    }
}
