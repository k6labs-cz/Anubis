using System.ComponentModel.DataAnnotations;

namespace k6labs.Anubis.Localization;

// DataAnnotations attributes whose messages come from Resources/Translations.resx, so form
// validation is translated. Pass the key with nameof(Translations.X) to keep it compile-checked.

public sealed class LRequiredAttribute : RequiredAttribute
{
    public LRequiredAttribute(string key) => Localize(this, key);

    internal static void Localize(ValidationAttribute attribute, string key)
    {
        attribute.ErrorMessageResourceType = typeof(Translations);
        attribute.ErrorMessageResourceName = key;
    }
}

public sealed class LEmailAddressAttribute : DataTypeAttribute
{
    private static readonly EmailAddressAttribute Inner = new();

    public LEmailAddressAttribute()
        : base(DataType.EmailAddress) => LRequiredAttribute.Localize(this, nameof(Translations.Validation_EmailInvalid));

    public override bool IsValid(object? value) => Inner.IsValid(value);
}

public sealed class LMinLengthAttribute : MinLengthAttribute
{
    public LMinLengthAttribute(int length, string key)
        : base(length) => LRequiredAttribute.Localize(this, key);
}

public sealed class LStringLengthAttribute : StringLengthAttribute
{
    public LStringLengthAttribute(int maximumLength, string key)
        : base(maximumLength) => LRequiredAttribute.Localize(this, key);
}

public sealed class LCompareAttribute : CompareAttribute
{
    public LCompareAttribute(string otherProperty, string key)
        : base(otherProperty) => LRequiredAttribute.Localize(this, key);
}

public sealed class LRegularExpressionAttribute : RegularExpressionAttribute
{
    public LRegularExpressionAttribute(string pattern, string key)
        : base(pattern) => LRequiredAttribute.Localize(this, key);
}
