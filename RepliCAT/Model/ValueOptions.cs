using System.Globalization;
using System.Reflection;
using System.Text;

namespace RepliCAT.Model;

/// <summary>
/// Параметры значения, заданные атрибутами члена: квантование и допуск сравнения.
/// Для члена-значения применяются к самому значению, для члена-коллекции (шаги 7–8) — к значениям элементов.
/// </summary>
internal sealed class ValueOptions
{
    /// <summary>
    /// Параметры по умолчанию: без квантования, точное сравнение.
    /// </summary>
    public static readonly ValueOptions None = new(null, 0);

    public ValueOptions(QuantizeAttribute quantize, double tolerance)
    {
        Quantize = quantize;
        Tolerance = tolerance;
    }

    /// <summary>
    /// Параметры квантования или <c>null</c>.
    /// </summary>
    public QuantizeAttribute Quantize { get; }

    /// <summary>
    /// Допуск сравнения, 0 — точное сравнение.
    /// </summary>
    public double Tolerance { get; }

    /// <summary>
    /// <c>true</c>, если параметры не заданы (нет квантования и допуск равен нулю).
    /// </summary>
    public bool IsDefault => Quantize == null && Tolerance == 0;

    /// <summary>
    /// Читает параметры из атрибутов члена.
    /// </summary>
    /// <param name="member">Поле или свойство</param>
    /// <param name="replicated">Атрибут <see cref="ReplicatedAttribute"/> члена</param>
    public static ValueOptions FromMember(MemberInfo member, ReplicatedAttribute replicated)
    {
        var quantize = member.GetCustomAttribute<QuantizeAttribute>(false);
        double tolerance = replicated?.Tolerance ?? 0;
        if (quantize == null && tolerance == 0)
        {
            return None;
        }

        return new ValueOptions(quantize, tolerance);
    }

    /// <summary>
    /// Дописывает каноническое описание параметров (в инвариантной культуре, с точным форматом "R").
    /// </summary>
    public void AppendSchema(StringBuilder sb)
    {
        if (Quantize != null)
        {
            sb.Append(";q=");
            if (Quantize.IsBounded)
            {
                sb.Append(Quantize.Min.ToString("R", CultureInfo.InvariantCulture)).Append("..")
                    .Append(Quantize.Max.ToString("R", CultureInfo.InvariantCulture)).Append('/');
            }

            sb.Append(Quantize.Precision.ToString("R", CultureInfo.InvariantCulture));
        }

        if (Tolerance != 0)
        {
            sb.Append(";t=").Append(Tolerance.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
