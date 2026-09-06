import datetime
import decimal
import enum
import importlib.metadata
import json
import math
import re
import sys
import uuid


PROVIDER, MIMESIS_VERSION, TYPING_EXTENSIONS_VERSION, PROTOCOL_SCHEMA, CONFIGURATION_SCHEMA = sys.argv[1:]
REQUEST_ID = re.compile(r"^csimcatalogrequest1_[0-9a-f]{64}$")
PROVIDER_FIELD = re.compile(r"^(?!__)[A-Za-z_][A-Za-z0-9_]*(?:\.(?!__)[A-Za-z_][A-Za-z0-9_]*)+$")
DECIMAL_MAX = decimal.Decimal("79228162514264337593543950335")


def object_without_duplicates(pairs):
    result = {}
    for name, value in pairs:
        if name in result:
            raise ValueError(f"duplicate JSON property '{name}'")
        result[name] = value
    return result


def require_exact_properties(value, expected, location):
    if type(value) is not dict:
        raise ValueError(f"{location} must be an object")
    actual = set(value)
    if actual != expected:
        raise ValueError(f"{location} properties do not match the closed schema")


def require_package(package, expected):
    actual = importlib.metadata.version(package)
    if actual != expected:
        raise RuntimeError(f"{package} version '{actual}' does not match required version '{expected}'")


def normalize(value):
    if value is None or type(value) in (bool, str, int, float, decimal.Decimal):
        if type(value) is float and not math.isfinite(value):
            raise ValueError("Mimesis returned a non-finite number")
        return value
    if isinstance(value, enum.Enum):
        return normalize(value.value)
    if isinstance(value, uuid.UUID):
        return str(value)
    if isinstance(value, datetime.datetime):
        return value.isoformat()
    if isinstance(value, (datetime.date, datetime.time)):
        return value.isoformat()
    if isinstance(value, (list, tuple)):
        return [normalize(item) for item in value]
    if isinstance(value, dict):
        result = {}
        for name, item in value.items():
            if type(name) is not str:
                raise ValueError("Mimesis returned an object with a non-string key")
            result[name] = normalize(item)
        return result
    raise ValueError(f"Mimesis returned unsupported value type '{type(value).__name__}'")


def encode_number(value):
    if type(value) is int:
        number = decimal.Decimal(value)
    elif type(value) is float:
        if not math.isfinite(value):
            raise ValueError("Mimesis returned a non-finite number")
        number = decimal.Decimal(repr(value))
    else:
        number = value

    if not number.is_finite() or abs(number) > DECIMAL_MAX:
        raise ValueError("Mimesis returned a number outside the portable Decimal range")
    text = format(number, "f")
    if "." in text:
        text = text.rstrip("0").rstrip(".")
    if text in ("", "-0"):
        return "0"
    fractional = len(text.split(".", 1)[1]) if "." in text else 0
    significant = len(text.lstrip("-").replace(".", "").lstrip("0"))
    if fractional > 28 or significant > 29:
        raise ValueError("Mimesis returned a number outside the portable Decimal precision")
    return text


def ordinal_key(value):
    return value.encode("utf-16-be", errors="surrogatepass")


def encode(value):
    if value is None:
        return "null"
    if type(value) is bool:
        return "true" if value else "false"
    if type(value) in (int, float, decimal.Decimal):
        return encode_number(value)
    if type(value) is str:
        return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    if type(value) is list:
        return "[" + ",".join(encode(item) for item in value) + "]"
    if type(value) is dict:
        return "{" + ",".join(
            encode(name) + ":" + encode(value[name])
            for name in sorted(value, key=ordinal_key)
        ) + "}"
    raise ValueError(f"provider normalization retained unsupported type '{type(value).__name__}'")


def main():
    require_package("mimesis", MIMESIS_VERSION)
    require_package("typing_extensions", TYPING_EXTENSIONS_VERSION)
    from mimesis import Field

    request = json.loads(sys.stdin.read(), object_pairs_hook=object_without_duplicates)
    require_exact_properties(request, {
        "catalogId", "catalogRevision", "configuration", "count", "dateTimeReferenceUtc",
        "locale", "requestId", "schemaVersion", "seed", "valueType"
    }, "request")
    if request["schemaVersion"] != PROTOCOL_SCHEMA:
        raise ValueError("request schema version is unsupported")
    if type(request["requestId"]) is not str or not REQUEST_ID.fullmatch(request["requestId"]):
        raise ValueError("request identity is invalid")
    if type(request["count"]) is not int or request["count"] <= 0:
        raise ValueError("request count must be a positive integer")
    if type(request["seed"]) is not str or not re.fullmatch(r"-?(?:0|[1-9][0-9]*)", request["seed"]):
        raise ValueError("request seed is not a canonical integer string")
    seed = int(request["seed"])
    if seed < -(2 ** 63) or seed > 2 ** 63 - 1:
        raise ValueError("request seed is outside the signed 64-bit range")
    if type(request["locale"]) is not str or not request["locale"]:
        raise ValueError("request locale is required")
    if request["dateTimeReferenceUtc"] is not None:
        raise ValueError("Mimesis provider does not claim a fixed date-time reference")
    if type(request["valueType"]) is not dict:
        raise ValueError("request valueType must be an object")

    configuration = request["configuration"]
    require_exact_properties(configuration, {"members", "schemaVersion"}, "configuration")
    if configuration["schemaVersion"] != CONFIGURATION_SCHEMA:
        raise ValueError("Mimesis configuration schema version is unsupported")
    members = configuration["members"]
    if type(members) is not list or not members:
        raise ValueError("Mimesis configuration requires members")

    validated_members = []
    paths = set()
    for index, member in enumerate(members):
        location = f"configuration.members[{index}]"
        require_exact_properties(member, {"arguments", "field", "path"}, location)
        if type(member["arguments"]) is not dict:
            raise ValueError(f"{location}.arguments must be an object")
        if type(member["field"]) is not str or not PROVIDER_FIELD.fullmatch(member["field"]):
            raise ValueError(f"{location}.field must be fully qualified")
        if (type(member["path"]) is not list or len(member["path"]) != 1
                or type(member["path"][0]) is not str or not member["path"][0]):
            raise ValueError(f"{location}.path must contain one field name")
        path = member["path"][0]
        if path in paths:
            raise ValueError(f"duplicate member path '{path}'")
        paths.add(path)
        validated_members.append((path, member["field"], member["arguments"]))

    field = Field(locale=request["locale"], seed=seed)
    values = []
    for _ in range(request["count"]):
        record = {}
        for path, provider_field, arguments in validated_members:
            record[path] = normalize(field(provider_field, **arguments))
        values.append(record)

    response = {
        "provider": PROVIDER,
        "providerVersion": MIMESIS_VERSION,
        "requestId": request["requestId"],
        "schemaVersion": PROTOCOL_SCHEMA,
        "values": values,
    }
    sys.stdout.write(encode(response))


try:
    main()
except Exception as error:
    sys.stderr.write(f"Mimesis provider failed: {type(error).__name__}: {error}\n")
    raise SystemExit(1)
