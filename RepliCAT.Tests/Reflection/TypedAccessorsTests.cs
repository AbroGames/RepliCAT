using System.Reflection;
using Godot;
using RepliCAT.Reflection;

namespace RepliCAT.Tests.Reflection;

public class TypedAccessorsTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private class Base
    {
        public int PublicField = 0;
        private int _privateField;
        protected int ProtectedField;

        public int PublicProperty { get; set; }
        private int PrivateProperty { get; set; }
        protected int ProtectedProperty { get; set; }
        public int PrivateSetProperty { get; private set; }

        public int PrivateFieldValue
        {
            get => _privateField;
            set => _privateField = value;
        }

        public int PrivatePropertyValue
        {
            get => PrivateProperty;
            set => PrivateProperty = value;
        }

        public int ProtectedFieldValue
        {
            get => ProtectedField;
            set => ProtectedField = value;
        }

        public int ProtectedPropertyValue
        {
            get => ProtectedProperty;
            set => ProtectedProperty = value;
        }

        public void SetPrivateSetProperty(int value)
        {
            PrivateSetProperty = value;
        }
    }

    private sealed class Derived : Base
    {
        public string DerivedField = null;
    }

    private sealed class Holder
    {
        public const int Literal = 42;
        public static int StaticField = 0;

        public readonly int ReadonlyField = 7;
        public Vector3 StructField;
        public Vector2 StructProperty { get; set; }
        public int GetOnlyProperty { get; } = 5;
        public int ComputedProperty => 11;
        public int InitProperty { get; init; }
        public int SetterCalls;
        public object ObjectField = null;

        private int _backing;

        public int CountingProperty
        {
            get => _backing;
            set
            {
                _backing = value;
                SetterCalls++;
            }
        }

        public int this[int index]
        {
            get => index;
            set { }
        }
    }

    private enum Mode
    {
        First,
        Second = 5
    }

    private class PrivateAccessorsBase
    {
        public const Mode EnumLiteral = Mode.Second;

        public int PrivateSet { get; private set; }
        protected int PrivateGet { private get; set; }

        public int PrivateGetValue => PrivateGet;
    }

    private sealed class PrivateAccessorsDerived : PrivateAccessorsBase
    {
    }

    private struct ValueOwner
    {
        public int Field;
    }

    private static MemberInfo Member<T>(string name)
    {
        return typeof(T).GetMember(name, All).Single();
    }

    [Theory]
    [InlineData(nameof(Base.PublicField))]
    [InlineData("_privateField")]
    [InlineData("ProtectedField")]
    [InlineData(nameof(Base.PublicProperty))]
    [InlineData("PrivateProperty")]
    [InlineData("ProtectedProperty")]
    [InlineData(nameof(Base.PrivateSetProperty))]
    public void GetterAndSetter_WorkForAllAccessLevels(string name)
    {
        var member = Member<Base>(name);
        var getter = TypedAccessors.CreateGetter<Base, int>(member);
        var setter = TypedAccessors.CreateSetter<Base, int>(member);
        var target = new Base();

        Assert.True(TypedAccessors.CanWrite(member));
        Assert.NotNull(setter);

        setter(target, 123);

        Assert.Equal(123, getter(target));
        Assert.Equal(123, ReadThroughPublicApi(target, name));
    }

    private static int ReadThroughPublicApi(Base target, string name)
    {
        return name switch
        {
            nameof(Base.PublicField) => target.PublicField,
            "_privateField" => target.PrivateFieldValue,
            "ProtectedField" => target.ProtectedFieldValue,
            nameof(Base.PublicProperty) => target.PublicProperty,
            "PrivateProperty" => target.PrivatePropertyValue,
            "ProtectedProperty" => target.ProtectedPropertyValue,
            nameof(Base.PrivateSetProperty) => target.PrivateSetProperty,
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    [Theory]
    [InlineData(nameof(Base.PublicField))]
    [InlineData("_privateField")]
    [InlineData("ProtectedField")]
    [InlineData(nameof(Base.PublicProperty))]
    [InlineData("PrivateProperty")]
    [InlineData("ProtectedProperty")]
    [InlineData(nameof(Base.PrivateSetProperty))]
    public void BaseMembers_WorkThroughDerivedOwner(string name)
    {
        var member = Member<Base>(name);
        var getter = TypedAccessors.CreateGetter<Derived, int>(member);
        var setter = TypedAccessors.CreateSetter<Derived, int>(member);
        var target = new Derived();

        setter(target, -5);

        Assert.Equal(-5, getter(target));
        Assert.Equal(-5, ReadThroughPublicApi(target, name));
    }

    [Fact]
    public void DerivedMember_WorksThroughObjectOwner()
    {
        var member = Member<Derived>(nameof(Derived.DerivedField));
        var getter = TypedAccessors.CreateGetter<object, string>(member);
        var setter = TypedAccessors.CreateSetter<object, string>(member);
        var target = new Derived();

        setter(target, "abc");

        Assert.Equal("abc", target.DerivedField);
        Assert.Equal("abc", getter(target));
    }

    [Fact]
    public void PrivateSetter_IsUsed()
    {
        var member = Member<Base>(nameof(Base.PrivateSetProperty));
        var target = new Base();
        target.SetPrivateSetProperty(3);

        TypedAccessors.CreateSetter<Base, int>(member)(target, 9);

        Assert.Equal(9, target.PrivateSetProperty);
    }

    [Fact]
    public void PropertySetter_IsInvoked()
    {
        var member = Member<Holder>(nameof(Holder.CountingProperty));
        var setter = TypedAccessors.CreateSetter<Holder, int>(member);
        var target = new Holder();

        setter(target, 1);
        setter(target, 2);

        Assert.Equal(2, target.SetterCalls);
        Assert.Equal(2, target.CountingProperty);
    }

    [Fact]
    public void StructValues_RoundTrip()
    {
        var target = new Holder();
        var field = Member<Holder>(nameof(Holder.StructField));
        var property = Member<Holder>(nameof(Holder.StructProperty));

        TypedAccessors.CreateSetter<Holder, Vector3>(field)(target, new Vector3(1, 2, 3));
        TypedAccessors.CreateSetter<Holder, Vector2>(property)(target, new Vector2(4, 5));

        Assert.Equal(new Vector3(1, 2, 3), target.StructField);
        Assert.Equal(new Vector2(4, 5), target.StructProperty);
        Assert.Equal(new Vector3(1, 2, 3), TypedAccessors.CreateGetter<Holder, Vector3>(field)(target));
        Assert.Equal(new Vector2(4, 5), TypedAccessors.CreateGetter<Holder, Vector2>(property)(target));
    }

    [Fact]
    public void ValueConversion_BoxesAndUnboxesWhenTypesDiffer()
    {
        var target = new Holder { StructField = new Vector3(7, 8, 9) };
        var field = Member<Holder>(nameof(Holder.StructField));

        object boxed = TypedAccessors.CreateGetter<object, object>(field)(target);
        TypedAccessors.CreateSetter<object, object>(field)(target, new Vector3(-1, -2, -3));

        Assert.Equal(new Vector3(7, 8, 9), boxed);
        Assert.Equal(new Vector3(-1, -2, -3), target.StructField);

        var objectField = Member<Holder>(nameof(Holder.ObjectField));
        TypedAccessors.CreateSetter<Holder, string>(objectField)(target, "text");
        Assert.Equal("text", target.ObjectField);
    }

    [Fact]
    public void ReadonlyField_HasNullSetter()
    {
        var member = Member<Holder>(nameof(Holder.ReadonlyField));

        Assert.False(TypedAccessors.CanWrite(member));
        Assert.Null(TypedAccessors.CreateSetter<Holder, int>(member));
        Assert.Equal(7, TypedAccessors.CreateGetter<Holder, int>(member)(new Holder()));
    }

    [Fact]
    public void LiteralField_HasNullSetterAndReadableValue()
    {
        var member = Member<Holder>(nameof(Holder.Literal));

        Assert.False(TypedAccessors.CanWrite(member));
        Assert.Null(TypedAccessors.CreateSetter<Holder, int>(member));
        Assert.Equal(42, TypedAccessors.CreateGetter<Holder, int>(member)(null));
    }

    [Fact]
    public void EnumLiteralField_ReturnsEnumValue()
    {
        var member = typeof(PrivateAccessorsBase).GetField(nameof(PrivateAccessorsBase.EnumLiteral));

        Assert.Equal(Mode.Second, TypedAccessors.CreateGetter<PrivateAccessorsBase, Mode>(member)(null));
        Assert.Equal(5, TypedAccessors.CreateGetter<object, int>(member)(null));
    }

    [Fact]
    public void BasePrivateAccessors_WorkWhenPropertyIsReflectedFromDerivedType()
    {
        // PropertyInfo, полученный через наследника, сам по себе не видит приватных аксессоров базового типа
        var privateSet = typeof(PrivateAccessorsDerived).GetProperty(nameof(PrivateAccessorsBase.PrivateSet), All);
        var privateGet = typeof(PrivateAccessorsDerived).GetProperty("PrivateGet", All);
        var target = new PrivateAccessorsDerived();

        Assert.NotEqual(privateSet.DeclaringType, privateSet.ReflectedType);
        Assert.True(TypedAccessors.CanWrite(privateSet));

        TypedAccessors.CreateSetter<PrivateAccessorsDerived, int>(privateSet)(target, 4);
        TypedAccessors.CreateSetter<PrivateAccessorsDerived, int>(privateGet)(target, 8);

        Assert.Equal(4, target.PrivateSet);
        Assert.Equal(8, target.PrivateGetValue);
        Assert.Equal(4, TypedAccessors.CreateGetter<PrivateAccessorsDerived, int>(privateSet)(target));
        Assert.Equal(8, TypedAccessors.CreateGetter<PrivateAccessorsDerived, int>(privateGet)(target));
    }

    [Fact]
    public void StaticField_IgnoresOwner()
    {
        var member = Member<Holder>(nameof(Holder.StaticField));

        TypedAccessors.CreateSetter<Holder, int>(member)(null, 17);

        Assert.Equal(17, Holder.StaticField);
        Assert.Equal(17, TypedAccessors.CreateGetter<Holder, int>(member)(null));
    }

    [Theory]
    [InlineData(nameof(Holder.GetOnlyProperty), 5)]
    [InlineData(nameof(Holder.ComputedProperty), 11)]
    public void GetOnlyProperty_HasNullSetter(string name, int expected)
    {
        var member = Member<Holder>(name);

        Assert.False(TypedAccessors.CanWrite(member));
        Assert.Null(TypedAccessors.CreateSetter<Holder, int>(member));
        Assert.Equal(expected, TypedAccessors.CreateGetter<Holder, int>(member)(new Holder()));
    }

    [Fact]
    public void InitOnlyProperty_HasWorkingSetter()
    {
        var member = Member<Holder>(nameof(Holder.InitProperty));
        var target = new Holder { InitProperty = 1 };

        Assert.True(TypedAccessors.CanWrite(member));

        TypedAccessors.CreateSetter<Holder, int>(member)(target, 99);

        Assert.Equal(99, target.InitProperty);
        Assert.Equal(99, TypedAccessors.CreateGetter<Holder, int>(member)(target));
    }

    [Fact]
    public void StructOwner_GetterWorks_SetterRejected()
    {
        var member = Member<ValueOwner>(nameof(ValueOwner.Field));
        var value = new ValueOwner { Field = 12 };

        Assert.Equal(12, TypedAccessors.CreateGetter<ValueOwner, int>(member)(value));
        Assert.Equal(12, TypedAccessors.CreateGetter<object, int>(member)(value));
        Assert.Throws<ArgumentException>(() => TypedAccessors.CreateSetter<ValueOwner, int>(member));
    }

    [Fact]
    public void InvalidMembers_Throw()
    {
        var indexer = typeof(Holder).GetProperties().Single(p => p.GetIndexParameters().Length > 0);
        var method = typeof(Holder).GetMethod(nameof(ToString));

        Assert.Throws<ArgumentNullException>(() => TypedAccessors.CreateGetter<Holder, int>(null));
        Assert.Throws<ArgumentNullException>(() => TypedAccessors.CanWrite(null));
        Assert.Throws<ArgumentException>(() => TypedAccessors.CreateGetter<Holder, int>(indexer));
        Assert.Throws<ArgumentException>(() => TypedAccessors.CreateSetter<Holder, int>(indexer));
        Assert.Throws<ArgumentException>(() => TypedAccessors.CreateGetter<Holder, int>(method));
        Assert.Throws<ArgumentException>(() => TypedAccessors.CanWrite(method));
    }

    [Fact]
    public void IncompatibleTypes_Throw()
    {
        var field = Member<Holder>(nameof(Holder.StructField));

        Assert.Throws<ArgumentException>(() => TypedAccessors.CreateGetter<Base, Vector3>(field));
        Assert.Throws<ArgumentException>(() => TypedAccessors.CreateGetter<Holder, string>(field));
        Assert.Throws<ArgumentException>(() => TypedAccessors.CreateSetter<Holder, string>(field));
    }
}
