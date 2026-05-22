namespace RepliCAT.Tests;

/// <summary>
/// Проверка "код не выделяет память" через <see cref="GC.GetAllocatedBytesForCurrentThread"/>.<br/>
/// Замер повторяется до <see cref="Attempts"/> раз, и проверка проходит, если хотя бы один замер равен нулю.
/// Рантайм изредка выделяет память в потоке теста однократно (например, при смене уровня JIT-компиляции
/// под нагрузкой параллельных тестов), и такая разовая аллокация не должна валить тест. Аллокация в самом
/// проверяемом коде повторяется при каждом выполнении тела и видна во всех замерах, поэтому тест ее ловит.
/// Тело должно быть идемпотентным в смысле аллокаций: повторный запуск проходит тот же путь.
/// </summary>
internal static class AllocationAssert
{
    public const int Attempts = 5;

    public static void DoesNotAllocate(Action body)
    {
        var measured = new long[Attempts];
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated == 0)
            {
                return;
            }

            measured[attempt] = allocated;
        }

        Assert.Fail($"the code allocated memory in every attempt: {string.Join(", ", measured)} bytes.");
    }
}
