using System.Globalization;
using FsCheck;
using FsCheck.Xunit;
using ObdGarage.Obd;
using ObdGarage.Obd.Pids;
using ObdGarage.UI;

namespace ObdGarage.Tests;

/// <summary>
/// Property-based tests (FsCheck) for the pure parsers between the outside world and the app:
/// the DTC byte codec, the ELM327 command allow-list and the form-field parsers. Each property
/// runs against hundreds of generated inputs instead of a handful of hand-picked examples.
/// </summary>
public class ParserPropertyTests
{
    // ---------------------------------------------------------------- Dtc

    [Property(MaxTest = 1000)]
    public bool Every_byte_pair_decodes_to_a_five_character_code_that_encodes_back_to_the_same_bytes(byte a, byte b)
    {
        var code = Dtc.Decode(a, b);
        var (encodedA, encodedB) = Dtc.Encode(code);
        return code.Length == 5 && "PCBU".Contains(code[0]) && encodedA == a && encodedB == b;
    }

    [Property(MaxTest = 500)]
    public bool DecodeAll_yields_one_code_per_non_filler_pair_and_ignores_a_trailing_odd_byte(byte[] payload)
    {
        var codes = Dtc.DecodeAll(payload);

        var expected = 0;
        for (var i = 0; i + 1 < payload.Length; i += 2)
        {
            if (payload[i] != 0 || payload[i + 1] != 0)
                expected++;
        }
        return codes.Count == expected && codes.All(c => c.Length == 5 && Dtc.Encode(c) != ((byte)0, (byte)0));
    }

    [Property(MaxTest = 500)]
    public bool Encode_accepts_a_code_or_refuses_it_with_an_argument_exception_never_anything_else(NonNull<string> code)
    {
        try
        {
            var (a, b) = Dtc.Encode(code.Get);
            return Dtc.Decode(a, b) == code.Get.ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    // Corner cases the generator practically never lands on by itself, pinned as examples.
    [Theory]
    [InlineData("P0G00")]
    [InlineData("P0-00")]
    [InlineData("P0٣٣٣")]
    public void Encode_refuses_non_hex_digits_with_an_argument_exception(string code)
    {
        Assert.ThrowsAny<ArgumentException>(() => Dtc.Encode(code));
    }

    [Theory]
    [InlineData("1e300")]
    [InlineData("-1e300")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ParseDecimal_returns_null_for_values_outside_the_decimal_range(string raw)
    {
        Assert.Null(Fmt.ParseDecimal(raw));
    }

    // ---------------------------------------------------------------- Elm327Client

    [Property(MaxTest = 500)]
    public bool The_command_allow_list_never_throws_and_admits_exactly_the_diagnostic_modes(NonNull<string> command, byte mode, byte pid)
    {
        Elm327Client.IsCommandAllowed(command.Get);

        var allowed = mode is 0x01 or 0x02 or 0x03 or 0x07 or 0x09;
        return Elm327Client.IsCommandAllowed($"{mode:X2}{pid:X2}") == allowed
            && Elm327Client.IsCommandAllowed($"{mode:x2} {pid:x2}") == allowed;
    }

    // ---------------------------------------------------------------- Fmt

    [Property(MaxTest = 500)]
    public bool Form_field_parsing_never_throws(string? raw)
    {
        // These sit directly behind form inputs: an exception here is a 500 for a typo.
        Fmt.ParseDouble(raw);
        Fmt.ParseDecimal(raw);
        Fmt.ParseInt(raw);
        Fmt.ParseDate(raw);
        Fmt.ParseDateTimeLocal(raw);
        return true;
    }

    [Property(MaxTest = 500)]
    public bool A_double_survives_the_round_trip_in_both_invariant_and_german_notation(NormalFloat value)
    {
        var d = value.Get;
        var invariant = d.ToString("R", CultureInfo.InvariantCulture);
        return Fmt.ParseDouble(invariant) == d && Fmt.ParseDouble(invariant.Replace('.', ',')) == d;
    }

    [Property(MaxTest = 500)]
    public bool An_int_survives_the_round_trip(int value) =>
        Fmt.ParseInt(value.ToString(CultureInfo.InvariantCulture)) == value;

    [Property(MaxTest = 500)]
    public bool A_date_survives_the_round_trip(NonNegativeInt dayNumber)
    {
        var date = DateOnly.FromDayNumber(dayNumber.Get % (DateOnly.MaxValue.DayNumber + 1));
        return Fmt.ParseDate(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) == date;
    }

    [Property(MaxTest = 500)]
    public bool A_duration_is_never_rendered_negative(long ticks)
    {
        var rendered = Fmt.Duration(TimeSpan.FromTicks(ticks));
        return !rendered.Contains('-') && rendered.Contains(" min");
    }
}
