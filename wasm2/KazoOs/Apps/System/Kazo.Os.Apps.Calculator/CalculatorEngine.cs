using System.Globalization;

namespace Kazo.Os.Apps.Calculator;

/// <summary>
/// Deterministic C# engine powering the Calculator application.
/// Implements arithmetic operations, unary functions (sqrt, square, negate, percentage),
/// memory registers, and bounded string formatting.
/// </summary>
public sealed class CalculatorEngine
{
    private const int MaxDigits = 12;

    /// <summary>The text shown on the display: the operand being typed, the last result, or <c>Error</c>.</summary>
    public string CurrentValue { get; private set; } = "0";
    /// <summary>The left operand kept while the user types the right one; empty before the first operator.</summary>
    public string PreviousValue { get; private set; } = "";
    /// <summary>The pending binary operator (<c>+</c>, <c>-</c>, <c>*</c> or <c>/</c>); empty when none is pending.</summary>
    public string Operator { get; private set; } = "";
    /// <summary>
    /// True when the next digit starts a new operand instead of extending <see cref="CurrentValue"/>
    /// (after an operator, equals, a unary function or a clear).
    /// </summary>
    public bool WaitingForOperand { get; private set; } = true;
    /// <summary>The memory register driven by the M+, M-, MR and MC keys.</summary>
    public double MemoryValue { get; private set; } = 0;

    /// <summary>Appends a digit to the current operand, or starts a new one; ignored past 12 digits.</summary>
    /// <param name="digit">The digit key pressed, <c>"0"</c> to <c>"9"</c>.</param>
    public void InputDigit(string digit)
    {
        if (WaitingForOperand)
        {
            CurrentValue = digit;
            WaitingForOperand = false;
        }
        else
        {
            if (CurrentValue.Replace("-", "").Replace(".", "").Length >= MaxDigits) return;
            CurrentValue = CurrentValue == "0" ? digit : CurrentValue + digit;
        }
    }

    /// <summary>Adds a decimal point to the current operand (or starts <c>0.</c>); a second point is ignored.</summary>
    public void InputDecimal()
    {
        if (WaitingForOperand)
        {
            CurrentValue = "0.";
            WaitingForOperand = false;
        }
        else if (!CurrentValue.Contains('.'))
        {
            CurrentValue += ".";
        }
    }

    /// <summary>
    /// Sets the pending operator. When an operator is already pending and a right operand was typed,
    /// the pending operation is evaluated first, so <c>2 + 3 *</c> shows <c>5</c> like a pocket calculator.
    /// </summary>
    /// <param name="nextOperator">The operator key pressed: <c>+</c>, <c>-</c>, <c>*</c> or <c>/</c>.</param>
    public void HandleOperator(string nextOperator)
    {
        if (!string.IsNullOrEmpty(Operator) && !WaitingForOperand)
        {
            Calculate();
        }
        else
        {
            PreviousValue = CurrentValue;
        }

        Operator = nextOperator;
        WaitingForOperand = true;
    }

    /// <summary>Evaluates the pending operation and clears it; does nothing without a pending operator and operand.</summary>
    public void HandleEquals()
    {
        if (string.IsNullOrEmpty(Operator) || WaitingForOperand) return;
        Calculate();
        Operator = "";
        WaitingForOperand = true;
    }

    /// <summary>
    /// Applies <see cref="Operator"/> to <see cref="PreviousValue"/> and <see cref="CurrentValue"/>,
    /// storing the result in both. Division by zero shows <c>Error</c>.
    /// </summary>
    public void Calculate()
    {
        if (!double.TryParse(PreviousValue, CultureInfo.InvariantCulture, out double prev) ||
            !double.TryParse(CurrentValue, CultureInfo.InvariantCulture, out double curr))
            return;

        double result = 0;
        switch (Operator)
        {
            case "+": result = prev + curr; break;
            case "-": result = prev - curr; break;
            case "*": result = prev * curr; break;
            case "/":
                if (curr == 0)
                {
                    CurrentValue = "Error";
                    WaitingForOperand = true;
                    return;
                }
                result = prev / curr;
                break;
            default: return;
        }

        CurrentValue = FormatResult(result);
        PreviousValue = CurrentValue;
    }

    /// <summary>Clear entry (CE): resets the current operand to <c>0</c> but keeps the pending operation.</summary>
    public void Clear()
    {
        CurrentValue = "0";
        WaitingForOperand = true;
    }

    /// <summary>All clear (AC): resets the operand, the left operand and the pending operator; memory is kept.</summary>
    public void AllClear()
    {
        CurrentValue = "0";
        PreviousValue = "";
        Operator = "";
        WaitingForOperand = true;
    }

    /// <summary>Flips the sign of the current value; zero is left unchanged.</summary>
    public void Negate()
    {
        if (double.TryParse(CurrentValue, CultureInfo.InvariantCulture, out double val) && val != 0)
        {
            CurrentValue = FormatResult(-val);
        }
    }

    /// <summary>Divides the current value by 100.</summary>
    public void Percentage()
    {
        if (double.TryParse(CurrentValue, CultureInfo.InvariantCulture, out double val))
        {
            CurrentValue = FormatResult(val / 100.0);
            WaitingForOperand = true;
        }
    }

    /// <summary>Replaces the current value with its square.</summary>
    public void Square()
    {
        if (double.TryParse(CurrentValue, CultureInfo.InvariantCulture, out double val))
        {
            CurrentValue = FormatResult(val * val);
            WaitingForOperand = true;
        }
    }

    /// <summary>Replaces the current value with its square root, or <c>Error</c> for a negative number.</summary>
    public void SquareRoot()
    {
        if (double.TryParse(CurrentValue, CultureInfo.InvariantCulture, out double val))
        {
            if (val < 0)
            {
                CurrentValue = "Error";
            }
            else
            {
                CurrentValue = FormatResult(Math.Sqrt(val));
            }
            WaitingForOperand = true;
        }
    }

    /// <summary>Resets the memory register to zero (MC).</summary>
    public void MemoryClear() => MemoryValue = 0;
    /// <summary>Copies the memory register into the display as the current operand (MR).</summary>
    public void MemoryRecall()
    {
        CurrentValue = FormatResult(MemoryValue);
        WaitingForOperand = false;
    }

    /// <summary>Adds the current value to the memory register (M+).</summary>
    public void MemoryAdd()
    {
        if (double.TryParse(CurrentValue, CultureInfo.InvariantCulture, out double val))
            MemoryValue += val;
    }

    /// <summary>Subtracts the current value from the memory register (M-).</summary>
    public void MemorySubtract()
    {
        if (double.TryParse(CurrentValue, CultureInfo.InvariantCulture, out double val))
            MemoryValue -= val;
    }

    /// <summary>Formats a result for the display with up to 12 significant digits, invariant culture.</summary>
    /// <param name="val">The value to format.</param>
    /// <returns>The formatted number, or <c>Error</c> for NaN or an infinity.</returns>
    public static string FormatResult(double val)
    {
        if (double.IsNaN(val) || double.IsInfinity(val)) return "Error";
        string str = val.ToString("G12", CultureInfo.InvariantCulture);
        return str;
    }
}
