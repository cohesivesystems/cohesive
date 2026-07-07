using System.Globalization;
using System.Numerics;

namespace Cohesive.Domain;

/// <summary>
/// ISO-4217-style currency code.
/// </summary>
public readonly record struct Currency
{
    /// <summary>
    /// Creates a currency from a 3-letter code.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public Currency(string code)
    {
        Code = NormalizeCode(code: code);
    }

    /// <summary>
    /// Currency code in uppercase canonical form.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Creates a currency value from a code.
    /// </summary>
    public static Currency Parse(string code) => new(code);

    /// <summary>
    /// Attempts to parse a currency code.
    /// </summary>
    public static bool TryParse(string? code, out Currency currency)
    {
        try
        {
            currency = Parse(code: code ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            currency = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Code;

    static string NormalizeCode(string code)
    {
        var normalized = Guard.RequireNotNullOrWhiteSpace(value: code).Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(static character => char.IsAsciiLetter(character)))
            throw new ArgumentException(
                message: $"Currency code '{code}' is invalid. Expected a 3-letter alphabetic code (for example, USD).",
                paramName: nameof(code));

        return normalized;
    }
}

/// <summary>
/// Known default minor-unit precision by currency.
/// </summary>
public static class CurrencyMinorUnits
{
    static readonly IReadOnlyDictionary<string, int> DigitsByCurrencyCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["JPY"] = 0,
        ["KRW"] = 0,
        ["VND"] = 0,
        ["BHD"] = 3,
        ["IQD"] = 3,
        ["JOD"] = 3,
        ["KWD"] = 3,
        ["LYD"] = 3,
        ["OMR"] = 3,
        ["TND"] = 3,
    };

    /// <summary>
    /// Returns default minor-unit digits for the currency.
    /// Unknown currencies default to 2 digits.
    /// </summary>
    public static int GetDefault(Currency currency) =>
        DigitsByCurrencyCode.GetValueOrDefault(currency.Code, 2);
}

/// <summary>
/// Small shared currency lookup tables.
/// </summary>
public static class CurrencyTable
{
    public static readonly Currency USD = Currency.Parse(code: "USD");

    public static readonly Currency MXN = Currency.Parse(code: "MXN");

    public static readonly Currency CAD = Currency.Parse(code: "CAD");

    public static readonly Currency EUR = Currency.Parse(code: "EUR");

    public static readonly Currency GBP = Currency.Parse(code: "GBP");

    public static readonly Currency JPY = Currency.Parse(code: "JPY");

    public static readonly Currency CNY = Currency.Parse(code: "CNY");

    /// <summary>
    /// Common currencies by display label.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Currency> Common = new Dictionary<string, Currency>(StringComparer.Ordinal)
    {
        ["USA"] = USD,
        ["Mexico"] = MXN,
        ["Canada"] = CAD,
        ["Euro"] = EUR,
        ["British Pound"] = GBP,
        ["Japan"] = JPY,
        ["China"] = CNY,
    };
}

/// <summary>
/// Monetary amount in a specific currency.
/// </summary>
public readonly record struct Money
    : IComparable<Money>,
        IFormattable,
        IAdditionOperators<Money, Money, Money>,
        ISubtractionOperators<Money, Money, Money>,
        IUnaryNegationOperators<Money, Money>,
        IMultiplyOperators<Money, decimal, Money>,
        IDivisionOperators<Money, decimal, Money>,
        IDivisionOperators<Money, Money, decimal>
{
    /// <summary>
    /// Creates a money amount.
    /// </summary>
    public Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>
    /// Amount in major units.
    /// </summary>
    public decimal Amount { get; }

    /// <summary>
    /// Amount currency.
    /// </summary>
    public Currency Currency { get; }

    /// <summary>
    /// Creates a money amount from a currency code.
    /// </summary>
    public static Money Of(decimal amount, string currencyCode) => new(amount: amount, currency: new Currency(code: currencyCode));

    /// <summary>
    /// Parses text in either "USD 12.34" or "12.34 USD" form.
    /// </summary>
    /// <exception cref="FormatException"></exception>
    public static Money Parse(string text, IFormatProvider? formatProvider = null)
    {
        if (TryParse(text: text, formatProvider: formatProvider, value: out var value))
            return value;

        throw new FormatException(
            message: "Money text must be in either 'CCC amount' or 'amount CCC' form (for example, 'USD 12.34').");
    }

    /// <summary>
    /// Attempts to parse text in either "USD 12.34" or "12.34 USD" form.
    /// </summary>
    public static bool TryParse(string? text, out Money value) =>
        TryParse(text: text, formatProvider: null, value: out value);

    /// <summary>
    /// Attempts to parse text in either "USD 12.34" or "12.34 USD" form.
    /// </summary>
    public static bool TryParse(string? text, IFormatProvider? formatProvider, out Money value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split(separator: [' ', '\t'], options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        var numberStyles = NumberStyles.Number | NumberStyles.AllowLeadingSign;
        var provider = formatProvider ?? CultureInfo.InvariantCulture;

        if (Currency.TryParse(code: parts[0], currency: out var leadingCurrency)
            && decimal.TryParse(s: parts[1], style: numberStyles, provider: provider, result: out var trailingAmount))
        {
            value = new Money(amount: trailingAmount, currency: leadingCurrency);
            return true;
        }

        if (Currency.TryParse(code: parts[1], currency: out var trailingCurrency)
            && decimal.TryParse(s: parts[0], style: numberStyles, provider: provider, result: out var leadingAmount))
        {
            value = new Money(amount: leadingAmount, currency: trailingCurrency);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates zero for a specific currency.
    /// </summary>
    public static Money Zero(Currency currency) => new(amount: 0m, currency: currency);

    /// <summary>
    /// Indicates whether both amounts share the same currency.
    /// </summary>
    public bool IsSameCurrency(Money other) => Currency == other.Currency;

    /// <summary>
    /// Rounds the amount using either explicit digits or default currency minor units.
    /// </summary>
    public Money RoundToMinorUnit(MidpointRounding midpointRounding = MidpointRounding.ToEven, int? digits = null)
    {
        var scale = digits ?? CurrencyMinorUnits.GetDefault(currency: Currency);
        return new(amount: decimal.Round(d: Amount, decimals: scale, mode: midpointRounding), currency: Currency);
    }

    /// <summary>
    /// Converts this amount using an explicit FX rate.
    /// </summary>
    public Money Convert(
        ExchangeRate rate,
        MidpointRounding midpointRounding = MidpointRounding.ToEven,
        int? targetMinorUnitDigits = null
        ) =>
        rate.Convert(amount: this, midpointRounding: midpointRounding, targetMinorUnitDigits: targetMinorUnitDigits);

    /// <inheritdoc />
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(
            left: this,
            right: other,
            operation: "compare");

        return Amount.CompareTo(other.Amount);
    }

    /// <inheritdoc />
    public override string ToString() => ToString(format: null, formatProvider: CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats this money value as "CCC amount" using the provided numeric format and provider.
    /// </summary>
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        $"{Currency.Code} {Amount.ToString(format, formatProvider ?? CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Formats this money value as "CCC amount" using the provided provider.
    /// </summary>
    public string ToString(IFormatProvider? formatProvider) => ToString(format: null, formatProvider: formatProvider);

    /// <summary>
    /// Applies a multiplicative rate.
    /// </summary>
    public Money ApplyRate(decimal rate) => this * rate;

    /// <summary>
    /// Adds a percentage where 10 means +10%.
    /// </summary>
    public Money AddPercent(decimal percent) => ApplyRate(rate: 1m + (percent / 100m));

    /// <summary>
    /// Subtracts a percentage where 10 means -10%.
    /// </summary>
    public Money SubtractPercent(decimal percent) => ApplyRate(rate: 1m - (percent / 100m));

    /// <summary>
    /// Returns absolute value while preserving currency.
    /// </summary>
    public static Money Abs(Money value) => new(amount: decimal.Abs(value.Amount), currency: value.Currency);

    /// <summary>
    /// Returns minimum of two money values in the same currency.
    /// </summary>
    public static Money Min(Money left, Money right) => left.CompareTo(other: right) <= 0 ? left : right;

    /// <summary>
    /// Returns maximum of two money values in the same currency.
    /// </summary>
    public static Money Max(Money left, Money right) => left.CompareTo(other: right) >= 0 ? left : right;

    /// <summary>
    /// Clamps a money value to a same-currency range.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public static Money Clamp(Money value, Money min, Money max)
    {
        EnsureSameCurrency(left: value, right: min, operation: "clamp");
        EnsureSameCurrency(left: value, right: max, operation: "clamp");

        if (min.CompareTo(other: max) > 0)
            throw new ArgumentException(message: "Clamp minimum must be less than or equal to maximum.", paramName: nameof(min));

        if (value.CompareTo(other: min) < 0)
            return min;

        return value.CompareTo(other: max) > 0 ? max : value;
    }

    /// <summary>
    /// Splits this amount across weighted parts with deterministic remainder distribution.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public IReadOnlyList<Money> Allocate(
        IReadOnlyList<decimal> weights,
        MidpointRounding midpointRounding = MidpointRounding.ToEven,
        int? digits = null
        )
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0)
            throw new ArgumentException(message: "At least one weight is required.", paramName: nameof(weights));

        if (weights.Any(static weight => weight < 0m))
            throw new ArgumentException(message: "Weights must be non-negative.", paramName: nameof(weights));

        var totalWeight = weights.Sum();
        if (totalWeight <= 0m)
            throw new ArgumentException(message: "At least one weight must be greater than zero.", paramName: nameof(weights));

        var scale = digits ?? CurrencyMinorUnits.GetDefault(currency: Currency);
        var scaleFactor = GetScaleFactor(digits: scale);
        var totalMinor = decimal.Round(d: Amount * scaleFactor, decimals: 0, mode: midpointRounding);
        var sign = totalMinor < 0m ? -1m : 1m;
        var absoluteMinor = decimal.Abs(totalMinor);

        var provisionalMinor = new decimal[weights.Count];
        var fractions = new decimal[weights.Count];
        var allocatedMinor = 0m;

        for (var index = 0; index < weights.Count; index++)
        {
            var exact = absoluteMinor * (weights[index] / totalWeight);
            var baseUnits = decimal.Floor(d: exact);
            provisionalMinor[index] = baseUnits;
            fractions[index] = exact - baseUnits;
            allocatedMinor += baseUnits;
        }

        var remainder = decimal.ToInt64(absoluteMinor - allocatedMinor);
        if (remainder > 0)
        {
            var indexesByRemainder = Enumerable.Range(start: 0, count: weights.Count)
                .OrderByDescending(keySelector: index => fractions[index])
                .ThenBy(keySelector: index => index)
                .ToArray();

            for (long step = 0; step < remainder; step++)
            {
                var index = indexesByRemainder[(int)(step % indexesByRemainder.Length)];
                provisionalMinor[index] += 1m;
            }
        }

        var results = new Money[weights.Count];
        for (var index = 0; index < results.Length; index++)
        {
            var amount = (provisionalMinor[index] * sign) / scaleFactor;
            results[index] = new Money(amount: amount, currency: Currency);
        }

        return results;
    }

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left: left, right: right, operation: "add");
        return new(amount: left.Amount + right.Amount, currency: left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left: left, right: right, operation: "subtract");
        return new(amount: left.Amount - right.Amount, currency: left.Currency);
    }

    public static Money operator -(Money value) => new(amount: -value.Amount, currency: value.Currency);

    public static Money operator *(Money value, decimal scalar) => new(amount: value.Amount * scalar, currency: value.Currency);

    public static Money operator *(decimal scalar, Money value) => value * scalar;

    /// <exception cref="DivideByZeroException"></exception>
    public static Money operator /(Money value, decimal scalar)
    {
        if (scalar == 0m)
            throw new DivideByZeroException(message: "Cannot divide money by zero.");

        return new(amount: value.Amount / scalar, currency: value.Currency);
    }

    /// <exception cref="DivideByZeroException"></exception>
    public static decimal operator /(Money left, Money right)
    {
        EnsureSameCurrency(left: left, right: right, operation: "divide");

        if (right.Amount == 0m)
            throw new DivideByZeroException(message: "Cannot divide money by zero.");

        return left.Amount / right.Amount;
    }

    static void EnsureSameCurrency(Money left, Money right, string operation)
    {
        if (left.Currency == right.Currency)
            return;

        throw new InvalidOperationException(
            message: $"Cannot {operation} money values with different currencies ('{left.Currency.Code}' and '{right.Currency.Code}').");
    }

    static decimal GetScaleFactor(int digits)
    {
        if (digits < 0)
            throw new ArgumentOutOfRangeException(paramName: nameof(digits), message: "Scale digits must be greater than or equal to zero.");

        var factor = 1m;
        for (var i = 0; i < digits; i++)
            factor *= 10m;

        return factor;
    }
}

/// <summary>
/// Explicit FX rate between two currencies at a point in time.
/// </summary>
public readonly record struct ExchangeRate
{
    /// <summary>
    /// Creates an FX rate where <see cref="Rate"/> represents quote-per-base.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public ExchangeRate(Currency baseCurrency, Currency quoteCurrency, decimal rate, DateTimeOffset asOfUtc, string? source = null)
    {
        if (baseCurrency == quoteCurrency)
            throw new ArgumentException(message: "Base and quote currency must differ.", paramName: nameof(quoteCurrency));

        if (rate <= 0m)
            throw new ArgumentException(message: "Exchange rate must be greater than zero.", paramName: nameof(rate));

        BaseCurrency = baseCurrency;
        QuoteCurrency = quoteCurrency;
        Rate = rate;
        AsOfUtc = asOfUtc;
        Source = source;
    }

    /// <summary>
    /// Base currency in the pair.
    /// </summary>
    public Currency BaseCurrency { get; }

    /// <summary>
    /// Quote currency in the pair.
    /// </summary>
    public Currency QuoteCurrency { get; }

    /// <summary>
    /// Quote-per-base multiplier.
    /// </summary>
    public decimal Rate { get; }

    /// <summary>
    /// Timestamp for this rate.
    /// </summary>
    public DateTimeOffset AsOfUtc { get; }

    /// <summary>
    /// Optional provider/source label.
    /// </summary>
    public string? Source { get; }

    /// <summary>
    /// Converts an amount from base to quote currency and rounds to target minor units.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public Money Convert(
        Money amount,
        MidpointRounding midpointRounding = MidpointRounding.ToEven,
        int? targetMinorUnitDigits = null)
    {
        if (amount.Currency != BaseCurrency)
            throw new InvalidOperationException(
                message: $"Rate converts from '{BaseCurrency.Code}' to '{QuoteCurrency.Code}', but amount currency was '{amount.Currency.Code}'.");

        var converted = new Money(amount: amount.Amount * Rate, currency: QuoteCurrency);
        return converted.RoundToMinorUnit(midpointRounding: midpointRounding, digits: targetMinorUnitDigits);
    }

    /// <summary>
    /// Returns the reciprocal rate.
    /// </summary>
    public ExchangeRate Invert() =>
        new(
            baseCurrency: QuoteCurrency,
            quoteCurrency: BaseCurrency,
            rate: 1m / Rate,
            asOfUtc: AsOfUtc,
            source: Source);
}
