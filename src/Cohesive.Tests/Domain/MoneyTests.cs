using System.Globalization;

namespace Cohesive.Tests.Domain;

/// <summary>
/// Tests for <see cref="Money"/>, <see cref="Currency"/>, and <see cref="ExchangeRate"/>.
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Currency_Parse_NormalizesCodeToUppercase()
    {
        var currency = Currency.Parse(code: " usd ");

        Assert.Equal(expected: "USD", actual: currency.Code);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("US1")]
    [InlineData("USDD")]
    [InlineData("  ")]
    public void Currency_Parse_RejectsInvalidCode(string code)
    {
        Assert.Throws<ArgumentException>(() => Currency.Parse(code: code));
    }

    [Fact]
    public void CurrencyTable_Common_ContainsRequestedCurrencies()
    {
        Assert.Equal(expected: CurrencyTable.USD, actual: CurrencyTable.Common["USA"]);
        Assert.Equal(expected: CurrencyTable.MXN, actual: CurrencyTable.Common["Mexico"]);
        Assert.Equal(expected: CurrencyTable.CAD, actual: CurrencyTable.Common["Canada"]);
        Assert.Equal(expected: CurrencyTable.EUR, actual: CurrencyTable.Common["Euro"]);
        Assert.Equal(expected: CurrencyTable.GBP, actual: CurrencyTable.Common["British Pound"]);
        Assert.Equal(expected: CurrencyTable.JPY, actual: CurrencyTable.Common["Japan"]);
        Assert.Equal(expected: CurrencyTable.CNY, actual: CurrencyTable.Common["China"]);
    }

    [Fact]
    public void CurrencyTable_ExposesStaticMembersByCurrencyCode()
    {
        Assert.Equal(expected: Currency.Parse("USD"), actual: CurrencyTable.USD);
        Assert.Equal(expected: Currency.Parse("MXN"), actual: CurrencyTable.MXN);
        Assert.Equal(expected: Currency.Parse("CAD"), actual: CurrencyTable.CAD);
        Assert.Equal(expected: Currency.Parse("EUR"), actual: CurrencyTable.EUR);
        Assert.Equal(expected: Currency.Parse("GBP"), actual: CurrencyTable.GBP);
        Assert.Equal(expected: Currency.Parse("JPY"), actual: CurrencyTable.JPY);
        Assert.Equal(expected: Currency.Parse("CNY"), actual: CurrencyTable.CNY);
    }

    [Fact]
    public void Money_Addition_WithSameCurrency_Succeeds()
    {
        var total = Money.Of(amount: 10.25m, currencyCode: "USD") + Money.Of(amount: 4.75m, currencyCode: "USD");

        Assert.Equal(expected: Money.Of(amount: 15m, currencyCode: "USD"), actual: total);
    }

    [Fact]
    public void Money_Addition_WithDifferentCurrencies_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            testCode: () => _ = Money.Of(amount: 10m, currencyCode: "USD") + Money.Of(amount: 10m, currencyCode: "EUR"));
    }

    [Fact]
    public void Money_DivisionByMoney_WithSameCurrency_ReturnsRatio()
    {
        var ratio = Money.Of(amount: 50m, currencyCode: "USD") / Money.Of(amount: 20m, currencyCode: "USD");

        Assert.Equal(expected: 2.5m, actual: ratio);
    }

    [Fact]
    public void Money_DivisionByMoney_WithDifferentCurrencies_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            testCode: () => _ = Money.Of(amount: 10m, currencyCode: "USD") / Money.Of(amount: 5m, currencyCode: "EUR"));
    }

    [Fact]
    public void Money_Allocate_DistributesMinorUnitRemainderDeterministically()
    {
        var amount = Money.Of(amount: 10m, currencyCode: "USD");
        var parts = amount.Allocate(weights: [1m, 1m, 1m]);

        Assert.Equal(expected: 3, actual: parts.Count);
        Assert.Equal(expected: Money.Of(amount: 3.34m, currencyCode: "USD"), actual: parts[0]);
        Assert.Equal(expected: Money.Of(amount: 3.33m, currencyCode: "USD"), actual: parts[1]);
        Assert.Equal(expected: Money.Of(amount: 3.33m, currencyCode: "USD"), actual: parts[2]);
        Assert.Equal(expected: amount, actual: parts[0] + parts[1] + parts[2]);
    }

    [Fact]
    public void Money_Allocate_RespectsWeights()
    {
        var amount = Money.Of(amount: 1m, currencyCode: "USD");
        var parts = amount.Allocate(weights: [3m, 1m]);

        Assert.Equal(expected: Money.Of(amount: 0.75m, currencyCode: "USD"), actual: parts[0]);
        Assert.Equal(expected: Money.Of(amount: 0.25m, currencyCode: "USD"), actual: parts[1]);
    }

    [Fact]
    public void Money_Compare_WithDifferentCurrencies_Throws()
    {
        var usd = Money.Of(amount: 10m, currencyCode: "USD");
        var eur = Money.Of(amount: 9m, currencyCode: "EUR");

        Assert.Throws<InvalidOperationException>(testCode: () => _ = usd.CompareTo(other: eur));
    }

    [Fact]
    public void Money_MultiplyAndDivide_PreserveCurrency()
    {
        var amount = Money.Of(amount: 12m, currencyCode: "USD");
        var doubled = amount * 2m;
        var halved = doubled / 2m;

        Assert.Equal(expected: Money.Of(amount: 24m, currencyCode: "USD"), actual: doubled);
        Assert.Equal(expected: amount, actual: halved);
    }

    [Fact]
    public void Money_RateHelpers_ApplyRateAndPercents()
    {
        var amount = Money.Of(amount: 100m, currencyCode: "USD");

        Assert.Equal(expected: Money.Of(amount: 107.5m, currencyCode: "USD"), actual: amount.ApplyRate(rate: 1.075m));
        Assert.Equal(expected: Money.Of(amount: 110m, currencyCode: "USD"), actual: amount.AddPercent(percent: 10m));
        Assert.Equal(expected: Money.Of(amount: 90m, currencyCode: "USD"), actual: amount.SubtractPercent(percent: 10m));
    }

    [Fact]
    public void Money_Abs_Min_Max_Clamp_WorkWithSameCurrency()
    {
        var a = Money.Of(amount: -25m, currencyCode: "USD");
        var b = Money.Of(amount: 10m, currencyCode: "USD");
        var c = Money.Of(amount: 20m, currencyCode: "USD");

        Assert.Equal(expected: Money.Of(amount: 25m, currencyCode: "USD"), actual: Money.Abs(value: a));
        Assert.Equal(expected: b, actual: Money.Min(left: b, right: c));
        Assert.Equal(expected: c, actual: Money.Max(left: b, right: c));
        Assert.Equal(expected: b, actual: Money.Clamp(value: Money.Of(amount: 5m, currencyCode: "USD"), min: b, max: c));
        Assert.Equal(expected: c, actual: Money.Clamp(value: Money.Of(amount: 50m, currencyCode: "USD"), min: b, max: c));
        Assert.Equal(expected: Money.Of(amount: 15m, currencyCode: "USD"), actual: Money.Clamp(value: Money.Of(amount: 15m, currencyCode: "USD"), min: b, max: c));
    }

    [Fact]
    public void Money_RoundToMinorUnit_UsesCurrencyDefaults()
    {
        var usd = Money.Of(amount: 12.346m, currencyCode: "USD");
        var jpy = Money.Of(amount: 123.6m, currencyCode: "JPY");

        Assert.Equal(expected: 12.35m, actual: usd.RoundToMinorUnit().Amount);
        Assert.Equal(expected: 124m, actual: jpy.RoundToMinorUnit().Amount);
    }

    [Fact]
    public void Money_ParseAndTryParse_SupportCommonTextForms()
    {
        var fromCodeFirst = Money.Parse(text: "USD 12.34");
        var fromAmountFirst = Money.Parse(text: "12.34 USD");

        Assert.Equal(expected: Money.Of(amount: 12.34m, currencyCode: "USD"), actual: fromCodeFirst);
        Assert.Equal(expected: Money.Of(amount: 12.34m, currencyCode: "USD"), actual: fromAmountFirst);

        var ok = Money.TryParse(text: "USD 10.00", value: out var parsed);
        var invalid = Money.TryParse(text: "not-money", value: out _);

        Assert.True(condition: ok);
        Assert.Equal(expected: Money.Of(amount: 10m, currencyCode: "USD"), actual: parsed);
        Assert.False(condition: invalid);
    }

    [Fact]
    public void Money_ParseAndFormat_RespectFormatProviderAndFormatString()
    {
        var fr = CultureInfo.GetCultureInfo(name: "fr-FR");
        var parsed = Money.Parse(text: "EUR 12,5", formatProvider: fr);
        var formatted = Money.Of(amount: 12.3m, currencyCode: "USD").ToString(format: "0.00", formatProvider: CultureInfo.InvariantCulture);

        Assert.Equal(expected: Money.Of(amount: 12.5m, currencyCode: "EUR"), actual: parsed);
        Assert.Equal(expected: "USD 12.30", actual: formatted);
    }

    [Fact]
    public void ExchangeRate_Convert_UsesExplicitRate()
    {
        var rate = new ExchangeRate(
            baseCurrency: Currency.Parse("USD"),
            quoteCurrency: Currency.Parse("EUR"),
            rate: 0.91m,
            asOfUtc: new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.Zero),
            source: "test-feed");

        var converted = rate.Convert(amount: Money.Of(amount: 100m, currencyCode: "USD"));

        Assert.Equal(expected: Money.Of(amount: 91m, currencyCode: "EUR"), actual: converted);
    }

    [Fact]
    public void ExchangeRate_Convert_RejectsBaseCurrencyMismatch()
    {
        var rate = new ExchangeRate(
            baseCurrency: Currency.Parse("USD"),
            quoteCurrency: Currency.Parse("EUR"),
            rate: 0.91m,
            asOfUtc: new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.Zero));

        Assert.Throws<InvalidOperationException>(
            testCode: () => rate.Convert(amount: Money.Of(amount: 100m, currencyCode: "GBP")));
    }

    [Fact]
    public void ExchangeRate_Invert_ProducesReciprocalDirection()
    {
        var usdToEur = new ExchangeRate(
            baseCurrency: Currency.Parse("USD"),
            quoteCurrency: Currency.Parse("EUR"),
            rate: 0.8m,
            asOfUtc: new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.Zero));

        var eurToUsd = usdToEur.Invert();

        Assert.Equal(expected: Currency.Parse("EUR"), actual: eurToUsd.BaseCurrency);
        Assert.Equal(expected: Currency.Parse("USD"), actual: eurToUsd.QuoteCurrency);
        Assert.Equal(expected: 1.25m, actual: eurToUsd.Rate);
    }
}
