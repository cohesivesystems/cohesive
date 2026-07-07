namespace Cohesive.Prelude;

/// <summary>
/// Provides sequence partitioning helpers for values projected into Either unions.
/// </summary>
public static class EitherEnumerableExtensions
{
    extension<TSource>(IEnumerable<TSource> source)
    {
        /// <summary>
        /// Splits a sequence into two buckets using an <see cref="Either{TCase1,TCase2}"/> projection.
        /// </summary>
        public (IReadOnlyList<TCase1> Case1, IReadOnlyList<TCase2> Case2) Split<TCase1, TCase2>(Func<TSource, IEither<TCase1, TCase2>> selector)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);

            List<TCase1> case1Values = [];
            List<TCase2> case2Values = [];

            foreach (var item in source)
            {
                var either = selector(item);
                if (either.TryGetCase1(value: out var case1))
                {
                    case1Values.Add(item: case1);
                    continue;
                }

                if (either.TryGetCase2(value: out var case2))
                {
                    case2Values.Add(item: case2);
                    continue;
                }

                throw new InvalidOperationException(message: "Either value is uninitialized or has an unknown case.");
            }

            return (Case1: case1Values, Case2: case2Values);
        }

        /// <summary>
        /// Splits a sequence into three buckets using an <see cref="Either{TCase1,TCase2,TCase3}"/> projection.
        /// </summary>
        public (IReadOnlyList<TCase1> Case1, IReadOnlyList<TCase2> Case2, IReadOnlyList<TCase3> Case3) Split<TCase1, TCase2, TCase3>(Func<TSource, IEither<TCase1, TCase2, TCase3>> selector)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);

            List<TCase1> case1Values = [];
            List<TCase2> case2Values = [];
            List<TCase3> case3Values = [];

            foreach (var item in source)
            {
                var either = selector(item);
                if (either.TryGetCase1(value: out var case1))
                {
                    case1Values.Add(item: case1);
                    continue;
                }

                if (either.TryGetCase2(value: out var case2))
                {
                    case2Values.Add(item: case2);
                    continue;
                }

                if (either.TryGetCase3(value: out var case3))
                {
                    case3Values.Add(item: case3);
                    continue;
                }

                throw new InvalidOperationException(message: "Either value is uninitialized or has an unknown case.");
            }

            return (Case1: case1Values, Case2: case2Values, Case3: case3Values);
        }

        /// <summary>
        /// Splits a sequence into four buckets using an <see cref="Either{TCase1,TCase2,TCase3,TCase4}"/> projection.
        /// </summary>
        public (IReadOnlyList<TCase1> Case1, IReadOnlyList<TCase2> Case2, IReadOnlyList<TCase3> Case3, IReadOnlyList<TCase4> Case4) Split<TCase1, TCase2, TCase3, TCase4>(Func<TSource, IEither<TCase1, TCase2, TCase3, TCase4>> selector)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);

            List<TCase1> case1Values = [];
            List<TCase2> case2Values = [];
            List<TCase3> case3Values = [];
            List<TCase4> case4Values = [];

            foreach (var item in source)
            {
                var either = selector(item);
                if (either.TryGetCase1(value: out var case1))
                {
                    case1Values.Add(item: case1);
                    continue;
                }

                if (either.TryGetCase2(value: out var case2))
                {
                    case2Values.Add(item: case2);
                    continue;
                }

                if (either.TryGetCase3(value: out var case3))
                {
                    case3Values.Add(item: case3);
                    continue;
                }

                if (either.TryGetCase4(value: out var case4))
                {
                    case4Values.Add(item: case4);
                    continue;
                }

                throw new InvalidOperationException(message: "Either value is uninitialized or has an unknown case.");
            }

            return (Case1: case1Values, Case2: case2Values, Case3: case3Values, Case4: case4Values);
        }

        /// <summary>
        /// Splits a sequence into five buckets using an <see cref="Either{TCase1,TCase2,TCase3,TCase4,TCase5}"/> projection.
        /// </summary>
        public (IReadOnlyList<TCase1> Case1, IReadOnlyList<TCase2> Case2, IReadOnlyList<TCase3> Case3, IReadOnlyList<TCase4> Case4, IReadOnlyList<TCase5> Case5) Split<TCase1, TCase2, TCase3, TCase4, TCase5>(Func<TSource, IEither<TCase1, TCase2, TCase3, TCase4, TCase5>> selector)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);

            List<TCase1> case1Values = [];
            List<TCase2> case2Values = [];
            List<TCase3> case3Values = [];
            List<TCase4> case4Values = [];
            List<TCase5> case5Values = [];

            foreach (var item in source)
            {
                var either = selector(item);
                if (either.TryGetCase1(value: out var case1))
                {
                    case1Values.Add(item: case1);
                    continue;
                }

                if (either.TryGetCase2(value: out var case2))
                {
                    case2Values.Add(item: case2);
                    continue;
                }

                if (either.TryGetCase3(value: out var case3))
                {
                    case3Values.Add(item: case3);
                    continue;
                }

                if (either.TryGetCase4(value: out var case4))
                {
                    case4Values.Add(item: case4);
                    continue;
                }

                if (either.TryGetCase5(value: out var case5))
                {
                    case5Values.Add(item: case5);
                    continue;
                }

                throw new InvalidOperationException(message: "Either value is uninitialized or has an unknown case.");
            }

            return (Case1: case1Values, Case2: case2Values, Case3: case3Values, Case4: case4Values, Case5: case5Values);
        }

        /// <summary>
        /// Splits a sequence into six buckets using an <see cref="Either{TCase1,TCase2,TCase3,TCase4,TCase5,TCase6}"/> projection.
        /// </summary>
        public (IReadOnlyList<TCase1> Case1, IReadOnlyList<TCase2> Case2, IReadOnlyList<TCase3> Case3, IReadOnlyList<TCase4> Case4, IReadOnlyList<TCase5> Case5, IReadOnlyList<TCase6> Case6) Split<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6>(Func<TSource, IEither<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6>> selector)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);
            
            List<TCase1> case1Values = [];
            List<TCase2> case2Values = [];
            List<TCase3> case3Values = [];
            List<TCase4> case4Values = [];
            List<TCase5> case5Values = [];
            List<TCase6> case6Values = [];

            foreach (var item in source)
            {
                var either = selector(item);
                if (either.TryGetCase1(value: out var case1))
                {
                    case1Values.Add(item: case1);
                    continue;
                }

                if (either.TryGetCase2(value: out var case2))
                {
                    case2Values.Add(item: case2);
                    continue;
                }

                if (either.TryGetCase3(value: out var case3))
                {
                    case3Values.Add(item: case3);
                    continue;
                }

                if (either.TryGetCase4(value: out var case4))
                {
                    case4Values.Add(item: case4);
                    continue;
                }

                if (either.TryGetCase5(value: out var case5))
                {
                    case5Values.Add(item: case5);
                    continue;
                }

                if (either.TryGetCase6(value: out var case6))
                {
                    case6Values.Add(item: case6);
                    continue;
                }

                throw new InvalidOperationException(message: "Either value is uninitialized or has an unknown case.");
            }

            return (Case1: case1Values, Case2: case2Values, Case3: case3Values, Case4: case4Values, Case5: case5Values, Case6: case6Values);
        }

        /// <summary>
        /// Splits a sequence into seven buckets using an <see cref="Either{TCase1,TCase2,TCase3,TCase4,TCase5,TCase6,TCase7}"/> projection.
        /// </summary>
        public (IReadOnlyList<TCase1> Case1, IReadOnlyList<TCase2> Case2, IReadOnlyList<TCase3> Case3, IReadOnlyList<TCase4> Case4, IReadOnlyList<TCase5> Case5, IReadOnlyList<TCase6> Case6, IReadOnlyList<TCase7> Case7) Split<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6, TCase7>(Func<TSource, IEither<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6, TCase7>> selector)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);
            
            List<TCase1> case1Values = [];
            List<TCase2> case2Values = [];
            List<TCase3> case3Values = [];
            List<TCase4> case4Values = [];
            List<TCase5> case5Values = [];
            List<TCase6> case6Values = [];
            List<TCase7> case7Values = [];

            foreach (var item in source)
            {
                var either = selector(item);
                if (either.TryGetCase1(value: out var case1))
                {
                    case1Values.Add(item: case1);
                    continue;
                }

                if (either.TryGetCase2(value: out var case2))
                {
                    case2Values.Add(item: case2);
                    continue;
                }

                if (either.TryGetCase3(value: out var case3))
                {
                    case3Values.Add(item: case3);
                    continue;
                }

                if (either.TryGetCase4(value: out var case4))
                {
                    case4Values.Add(item: case4);
                    continue;
                }

                if (either.TryGetCase5(value: out var case5))
                {
                    case5Values.Add(item: case5);
                    continue;
                }

                if (either.TryGetCase6(value: out var case6))
                {
                    case6Values.Add(item: case6);
                    continue;
                }

                if (either.TryGetCase7(value: out var case7))
                {
                    case7Values.Add(item: case7);
                    continue;
                }

                throw new InvalidOperationException(message: "Either value is uninitialized or has an unknown case.");
            }

            return (Case1: case1Values, Case2: case2Values, Case3: case3Values, Case4: case4Values, Case5: case5Values, Case6: case6Values, Case7: case7Values);
        }

        /// <summary>
        /// Splits a sequence into eight buckets using an <see cref="Either{TCase1,TCase2,TCase3,TCase4,TCase5,TCase6,TCase7,TCase8}"/> projection.
        /// </summary>
        public (IReadOnlyList<TCase1> Case1, IReadOnlyList<TCase2> Case2, IReadOnlyList<TCase3> Case3, IReadOnlyList<TCase4> Case4, IReadOnlyList<TCase5> Case5, IReadOnlyList<TCase6> Case6, IReadOnlyList<TCase7> Case7, IReadOnlyList<TCase8> Case8) Split<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6, TCase7, TCase8>(Func<TSource, IEither<TCase1, TCase2, TCase3, TCase4, TCase5, TCase6, TCase7, TCase8>> selector)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);

            List<TCase1> case1Values = [];
            List<TCase2> case2Values = [];
            List<TCase3> case3Values = [];
            List<TCase4> case4Values = [];
            List<TCase5> case5Values = [];
            List<TCase6> case6Values = [];
            List<TCase7> case7Values = [];
            List<TCase8> case8Values = [];

            foreach (var item in source)
            {
                var either = selector(item);
                if (either.TryGetCase1(value: out var case1))
                {
                    case1Values.Add(item: case1);
                    continue;
                }

                if (either.TryGetCase2(value: out var case2))
                {
                    case2Values.Add(item: case2);
                    continue;
                }

                if (either.TryGetCase3(value: out var case3))
                {
                    case3Values.Add(item: case3);
                    continue;
                }

                if (either.TryGetCase4(value: out var case4))
                {
                    case4Values.Add(item: case4);
                    continue;
                }

                if (either.TryGetCase5(value: out var case5))
                {
                    case5Values.Add(item: case5);
                    continue;
                }

                if (either.TryGetCase6(value: out var case6))
                {
                    case6Values.Add(item: case6);
                    continue;
                }

                if (either.TryGetCase7(value: out var case7))
                {
                    case7Values.Add(item: case7);
                    continue;
                }

                if (either.TryGetCase8(value: out var case8))
                {
                    case8Values.Add(item: case8);
                    continue;
                }

                throw new InvalidOperationException(message: "Either value is uninitialized or has an unknown case.");
            }

            return (Case1: case1Values, Case2: case2Values, Case3: case3Values, Case4: case4Values, Case5: case5Values, Case6: case6Values, Case7: case7Values, Case8: case8Values);
        }
    }
}
