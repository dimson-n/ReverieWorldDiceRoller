﻿using System.Collections;

namespace RP.ReverieWorld
{
    public sealed partial class DiceRoller
    {
        public const int DefaultDiceFacesCount = 6;

        /// <summary>
        /// Usable for rerolls & bursts.
        /// </summary>
        public const int Infinite = -1;

        private readonly IRandomProvider randomProvider;
        private readonly IParameters defaultParameters;

        public interface IRandom : IDisposable
        {
            /// <summary>
            /// Same behavior as <see cref="System.Random.Next(int)"/> expected.
            /// </summary>
            /// <param name="maxValue">The exclusive upper bound of the random number to be generated. <paramref name="maxValue"/> must be greater than or equal to 0.</param>
            /// <returns></returns>
            int Next(int maxValue);
        }

        public interface IRandomProvider
        {
            IRandom Lock();
        }

        public interface IParameters
        {
            int FacesCount { get; }
            int DicesCount { get; }
            int AdditionalDicesCount { get; }
            int RerollsCount { get; }
            int BurstsCount { get; }
            int Bonus { get; }

            bool HasInfinityRerolls => RerollsCount < 0;
            bool HasInfinityBursts => BurstsCount < 0;
        }

        /// <summary>
        /// </summary>
        /// <param name="randomProvider">Implementation of <see cref="IRandomProvider"/> interface.</param>
        /// <param name="defaultParameters"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public DiceRoller(IRandomProvider randomProvider, IParameters? defaultParameters = null)
        {
            if (randomProvider is null)
            {
                throw new ArgumentNullException(nameof(randomProvider));
            }

            this.randomProvider = randomProvider;
            this.defaultParameters = defaultParameters ?? new Parameters();
            ValidateParameters(this.defaultParameters);
        }

        internal sealed class DiceData
        {
            public List<int> values;
            public bool removed;
            public bool burstMade;
            public bool isBurst;

            public int Value => values.Last();

            public DiceData(int value, bool removed = false, bool burstMade = false, bool isBurst = false)
            {
                this.values = new List<int>(1) { value };
                this.removed = removed;
                this.burstMade = burstMade;
                this.isBurst = isBurst;
            }
        }

        public sealed class Dice : IReadOnlyList<int>
        {
            private readonly DiceData data;

            public int Value { get; }
            public int RollsCount => data.values.Count;
            public bool WasRemoved => data.removed;
            public bool IsBurst => data.isBurst;

            internal Dice(DiceData data)
            {
                this.data = data;

                Value = data.Value;
            }

            public int this[int index] => data.values[index];

            public int Count => data.values.Count;

            public IEnumerator<int> GetEnumerator() => data.values.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)data.values).GetEnumerator();

            public override string ToString()
            {
                return $"{(WasRemoved ? "-" : string.Empty)}{(IsBurst ? "*" : string.Empty)}{Value}";
            }
        }

        public sealed class Result : IReadOnlyList<Dice>
        {
            private readonly IReadOnlyList<Dice> rolls;

            private readonly IParameters parameters;

            public int Total { get; }
            public int Bonus => parameters.Bonus;

            public int DiceFacesCount => parameters.FacesCount;
            public int BaseDicesCount => parameters.DicesCount;
            public int RemovedDicesCount => parameters.AdditionalDicesCount;
            public int InitialRerollsCount => parameters.RerollsCount;
            public int InitialBurstsCount => parameters.BurstsCount;

            public bool HasInfinityRerolls => parameters.HasInfinityRerolls;
            public bool HasInfinityBursts => parameters.HasInfinityBursts;

            internal Result(List<DiceData> data, IParameters parameters)
            {
                this.rolls = data.Select(d => new Dice(d)).ToArray();
                this.parameters = parameters;

                Total = rolls.Where(d => !d.WasRemoved).Sum(d => d.Value) + Bonus;
            }

            public Dice this[int index] => rolls[index];

            public int Count => rolls.Count;

            public IEnumerator<Dice> GetEnumerator() => rolls.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)rolls).GetEnumerator();
        }

        public Result Roll(IParameters? parameters = null)
        {
            parameters ??= defaultParameters;
            ValidateParameters(parameters);

            List<DiceData> data = new(parameters.DicesCount + parameters.AdditionalDicesCount + (parameters.HasInfinityBursts ? parameters.DicesCount : parameters.BurstsCount));

            using (var random = randomProvider.Lock())
            {
                int makeRoll()
                {
                    return random.Next(parameters.FacesCount) + 1;
                }

                {
                    int initialRollsCount = parameters.DicesCount + parameters.AdditionalDicesCount;
                    for (int i = 0; i != initialRollsCount; ++i)
                    {
                        data.Add(new DiceData(makeRoll()));
                    }
                }

                {
                    // TODO: can try boundary+=1 if there is much more rerolls then ones as if parameters.HasInfinityRerolls.
                    int nonRerollBoundary = parameters.FacesCount / 2 + (parameters.HasInfinityRerolls ? 1 : 0);

                    var sortedRolls = data.OrderBy(d => d.Value);
                    int mayBeRemoved = Math.Max(parameters.AdditionalDicesCount, sortedRolls.TakeWhile(d => d.Value < nonRerollBoundary).Count());
                    int rerollableCount = sortedRolls.TakeWhile(d => d.Value == 1).Count();
                    int skipToReroll = Math.Min(mayBeRemoved - parameters.AdditionalDicesCount,
                                                Math.Min(rerollableCount,
                                                         parameters.HasInfinityRerolls ? int.MaxValue : parameters.RerollsCount));
                    int takeFirstRerollable = rerollableCount - skipToReroll;

                    foreach (var d in sortedRolls.Where((_, i) => i < takeFirstRerollable || rerollableCount <= i)
                                                 .Take(parameters.AdditionalDicesCount))
                    {
                        d.removed = true;
                    }
                }

                {
                    bool somethingChanged = false;
                    int availableRerolls = parameters.RerollsCount;
                    int availableBursts = parameters.BurstsCount;
                    do
                    {
                        somethingChanged = false;

                        if (availableRerolls != 0)
                        {
                            var rerollsCount = parameters.HasInfinityRerolls ? int.MaxValue : availableRerolls;
                            foreach (var d in data.Where(d => !d.removed && d.Value == 1).Take(rerollsCount))
                            {
                                if (!parameters.HasInfinityRerolls)
                                {
                                    --availableRerolls;
                                }

                                d.values.Add(makeRoll());
                                somethingChanged = true;
                            }
                        }

                        if (availableBursts != 0)
                        {
                            var burstsCount = parameters.HasInfinityBursts ? int.MaxValue : availableBursts;
                            var toBurst = data.Where(d => !d.removed && !d.burstMade && d.Value == parameters.FacesCount).Take(burstsCount);
                            var newRolls = new List<DiceData>(toBurst.Count());

                            foreach (var d in toBurst)
                            {
                                if (!parameters.HasInfinityBursts)
                                {
                                    --availableBursts;
                                }

                                d.burstMade = true;

                                newRolls.Add(new DiceData(makeRoll(), isBurst: true));
                                somethingChanged = true;
                            }

                            data.AddRange(newRolls);
                        }
                    } while (somethingChanged);
                }
            }

            return new Result(data, parameters);
        }
    }
}
