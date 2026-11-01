// Minimal JSON reader/writer for the opennest_job CLI. Standard library only (the solver has no external
// dependencies, so the job runner keeps that property). Reads RFC 8259 JSON into a tree of `Value`s and
// writes numbers with 17 significant digits so doubles round-trip exactly.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace opennest::json {

struct Value;
using Array = std::vector<Value>;
using Object = std::vector<std::pair<std::string, Value>>;   // keeps key order for echoing

struct Value {
    enum class Kind { Null, Bool, Number, String, Array, Object };
    Kind kind = Kind::Null;
    bool b = false;
    double num = 0.0;
    std::string str;
    std::shared_ptr<Array> arr;
    std::shared_ptr<Object> obj;

    bool is_null() const { return kind == Kind::Null; }
    bool is_number() const { return kind == Kind::Number; }
    bool is_string() const { return kind == Kind::String; }
    bool is_array() const { return kind == Kind::Array; }
    bool is_object() const { return kind == Kind::Object; }
    bool is_bool() const { return kind == Kind::Bool; }

    const Array& as_array(const char* what) const {
        if (!is_array()) throw std::runtime_error(std::string(what) + " must be an array");
        return *arr;
    }
    double as_number(const char* what) const {
        if (!is_number()) throw std::runtime_error(std::string(what) + " must be a number");
        return num;
    }
    const std::string& as_string(const char* what) const {
        if (!is_string()) throw std::runtime_error(std::string(what) + " must be a string");
        return str;
    }
    // Object member lookup; nullptr when absent (or when this is not an object).
    const Value* find(const std::string& key) const {
        if (!is_object()) return nullptr;
        for (const auto& kv : *obj)
            if (kv.first == key) return &kv.second;
        return nullptr;
    }
};

class Parser {
public:
    explicit Parser(const std::string& text) : s_(text) {}

    Value parse() {
        skip_ws();
        Value v = parse_value(0);
        skip_ws();
        if (i_ != s_.size()) fail("trailing characters after the JSON document");
        return v;
    }

private:
    const std::string& s_;
    size_t i_ = 0;
    static constexpr int kMaxDepth = 64;

    [[noreturn]] void fail(const std::string& msg) const {
        size_t line = 1, col = 1;
        for (size_t k = 0; k < i_ && k < s_.size(); ++k) {
            if (s_[k] == '\n') { ++line; col = 1; } else { ++col; }
        }
        throw std::runtime_error("JSON parse error at line " + std::to_string(line) + ", column " +
                                 std::to_string(col) + ": " + msg);
    }
    void skip_ws() {
        while (i_ < s_.size() && (s_[i_] == ' ' || s_[i_] == '\t' || s_[i_] == '\n' || s_[i_] == '\r')) ++i_;
    }
    bool consume(const char* lit) {
        size_t n = std::char_traits<char>::length(lit);
        if (s_.compare(i_, n, lit) == 0) { i_ += n; return true; }
        return false;
    }

    Value parse_value(int depth) {
        if (depth > kMaxDepth) fail("nesting too deep");
        if (i_ >= s_.size()) fail("unexpected end of input");
        char c = s_[i_];
        Value v;
        if (c == '{') return parse_object(depth);
        if (c == '[') return parse_array(depth);
        if (c == '"') { v.kind = Value::Kind::String; v.str = parse_string(); return v; }
        if (consume("true")) { v.kind = Value::Kind::Bool; v.b = true; return v; }
        if (consume("false")) { v.kind = Value::Kind::Bool; v.b = false; return v; }
        if (consume("null")) return v;
        if (c == '-' || (c >= '0' && c <= '9')) { v.kind = Value::Kind::Number; v.num = parse_number(); return v; }
        fail(std::string("unexpected character '") + c + "'");
    }

    Value parse_object(int depth) {
        Value v;
        v.kind = Value::Kind::Object;
        v.obj = std::make_shared<Object>();
        ++i_;   // {
        skip_ws();
        if (i_ < s_.size() && s_[i_] == '}') { ++i_; return v; }
        for (;;) {
            skip_ws();
            if (i_ >= s_.size() || s_[i_] != '"') fail("expected an object key");
            std::string key = parse_string();
            skip_ws();
            if (i_ >= s_.size() || s_[i_] != ':') fail("expected ':'");
            ++i_;
            skip_ws();
            v.obj->emplace_back(std::move(key), parse_value(depth + 1));
            skip_ws();
            if (i_ < s_.size() && s_[i_] == ',') { ++i_; continue; }
            if (i_ < s_.size() && s_[i_] == '}') { ++i_; return v; }
            fail("expected ',' or '}'");
        }
    }

    Value parse_array(int depth) {
        Value v;
        v.kind = Value::Kind::Array;
        v.arr = std::make_shared<Array>();
        ++i_;   // [
        skip_ws();
        if (i_ < s_.size() && s_[i_] == ']') { ++i_; return v; }
        for (;;) {
            skip_ws();
            v.arr->push_back(parse_value(depth + 1));
            skip_ws();
            if (i_ < s_.size() && s_[i_] == ',') { ++i_; continue; }
            if (i_ < s_.size() && s_[i_] == ']') { ++i_; return v; }
            fail("expected ',' or ']'");
        }
    }

    double parse_number() {
        size_t start = i_;
        if (s_[i_] == '-') ++i_;
        auto digits = [&] { size_t d = i_; while (i_ < s_.size() && s_[i_] >= '0' && s_[i_] <= '9') ++i_; return i_ - d; };
        if (digits() == 0) fail("invalid number");
        if (i_ < s_.size() && s_[i_] == '.') { ++i_; if (digits() == 0) fail("invalid number"); }
        if (i_ < s_.size() && (s_[i_] == 'e' || s_[i_] == 'E')) {
            ++i_;
            if (i_ < s_.size() && (s_[i_] == '+' || s_[i_] == '-')) ++i_;
            if (digits() == 0) fail("invalid number");
        }
        // strtod is locale-sensitive for the decimal point; the CLI never calls setlocale, so the
        // "C" locale (decimal '.') is in effect.
        std::string tok = s_.substr(start, i_ - start);
        char* end = nullptr;
        double d = std::strtod(tok.c_str(), &end);
        if (end != tok.c_str() + tok.size()) fail("invalid number");
        return d;
    }

    static void append_utf8(std::string& out, uint32_t cp) {
        if (cp < 0x80) out += char(cp);
        else if (cp < 0x800) { out += char(0xC0 | (cp >> 6)); out += char(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) { out += char(0xE0 | (cp >> 12)); out += char(0x80 | ((cp >> 6) & 0x3F)); out += char(0x80 | (cp & 0x3F)); }
        else { out += char(0xF0 | (cp >> 18)); out += char(0x80 | ((cp >> 12) & 0x3F)); out += char(0x80 | ((cp >> 6) & 0x3F)); out += char(0x80 | (cp & 0x3F)); }
    }
    uint32_t parse_hex4() {
        if (i_ + 4 > s_.size()) fail("truncated \\u escape");
        uint32_t v = 0;
        for (int k = 0; k < 4; ++k) {
            char h = s_[i_++];
            v <<= 4;
            if (h >= '0' && h <= '9') v |= uint32_t(h - '0');
            else if (h >= 'a' && h <= 'f') v |= uint32_t(h - 'a' + 10);
            else if (h >= 'A' && h <= 'F') v |= uint32_t(h - 'A' + 10);
            else fail("invalid \\u escape");
        }
        return v;
    }
    std::string parse_string() {
        ++i_;   // opening quote
        std::string out;
        while (i_ < s_.size()) {
            char c = s_[i_++];
            if (c == '"') return out;
            if (static_cast<unsigned char>(c) < 0x20) fail("control character in string");
            if (c != '\\') { out += c; continue; }
            if (i_ >= s_.size()) break;
            char e = s_[i_++];
            switch (e) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    uint32_t cp = parse_hex4();
                    if (cp >= 0xD800 && cp <= 0xDBFF) {
                        if (!consume("\\u")) fail("unpaired surrogate");
                        uint32_t lo = parse_hex4();
                        if (lo < 0xDC00 || lo > 0xDFFF) fail("invalid surrogate pair");
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                    } else if (cp >= 0xDC00 && cp <= 0xDFFF) {
                        fail("unpaired surrogate");
                    }
                    append_utf8(out, cp);
                    break;
                }
                default: fail("invalid escape");
            }
        }
        fail("unterminated string");
    }
};

inline Value parse(const std::string& text) { return Parser(text).parse(); }

// ---- writing ----
inline std::string quote(const std::string& s) {
    std::string o = "\"";
    for (unsigned char c : s) {
        switch (c) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (c < 0x20) { char buf[8]; std::snprintf(buf, sizeof buf, "\\u%04x", c); o += buf; }
                else o += char(c);
        }
    }
    return o + "\"";
}

// Shortest-safe round-trip formatting: 17 significant digits, -0 written as 0, non-finite as null.
inline std::string number(double d) {
    if (!std::isfinite(d)) return "null";
    if (d == 0.0) return "0";
    char buf[32];
    std::snprintf(buf, sizeof buf, "%.17g", d);
    return buf;
}

inline std::string dump(const Value& v) {
    switch (v.kind) {
        case Value::Kind::Null: return "null";
        case Value::Kind::Bool: return v.b ? "true" : "false";
        case Value::Kind::Number: return number(v.num);
        case Value::Kind::String: return quote(v.str);
        case Value::Kind::Array: {
            std::string o = "[";
            for (size_t k = 0; k < v.arr->size(); ++k) { if (k) o += ","; o += dump((*v.arr)[k]); }
            return o + "]";
        }
        case Value::Kind::Object: {
            std::string o = "{";
            for (size_t k = 0; k < v.obj->size(); ++k) {
                if (k) o += ",";
                o += quote((*v.obj)[k].first) + ":" + dump((*v.obj)[k].second);
            }
            return o + "}";
        }
    }
    return "null";
}

}  // namespace opennest::json
