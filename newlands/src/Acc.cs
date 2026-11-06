using System;
using System.Collections.Generic;
using System.Reflection;

namespace KingdomBot.NewLands
{
    /// <summary>Reflection access to private game members (Unity 5.3's Mono enforces field/method visibility).</summary>
    public static class Acc
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();
        static readonly Dictionary<string, MethodInfo> _methods = new Dictionary<string, MethodInfo>();

        static FieldInfo Field(Type t, string name)
        {
            string key = t.FullName + "." + name;
            FieldInfo f;
            if (!_fields.TryGetValue(key, out f))
            {
                for (var tt = t; tt != null && f == null; tt = tt.BaseType) f = tt.GetField(name, BF);
                if (f == null) throw new MissingFieldException(t.Name, name);
                _fields[key] = f;
            }
            return f;
        }

        public static T Get<T>(object o, string name) { return (T)Field(o.GetType(), name).GetValue(o); }
        public static void Set(object o, string name, object value) { Field(o.GetType(), name).SetValue(o, value); }
        public static int GetInt(object o, string name) { return Convert.ToInt32(Field(o.GetType(), name).GetValue(o)); }

        public static object Call(object o, string name, params object[] args)
        {
            string key = o.GetType().FullName + "." + name;
            MethodInfo m;
            if (!_methods.TryGetValue(key, out m))
            {
                m = o.GetType().GetMethod(name, BF);
                if (m == null) throw new MissingMethodException(o.GetType().Name, name);
                _methods[key] = m;
            }
            return m.Invoke(o, args);
        }

        // convenience
        public static Wallet Wallet(object o) { return Get<Wallet>(o, "_wallet"); }
        public static int PayState(Player p) { return GetInt(p, "_payState"); }
        public static int ActionState(Player p) { return GetInt(p, "_actionState"); }
        public static Payable Selected(Player p) { return Get<Payable>(p, "_selectedPayable"); }
        public static int Count(object o, string name) { var c = Field(o.GetType(), name).GetValue(o) as System.Collections.ICollection; return c != null ? c.Count : 0; }

        static readonly string[] PayStateNames = { "None", "Holding", "Transaction", "Completed", "KeyDown", "HoldDetected", "KeyHold", "Cancelling" };
        static readonly string[] ActionNames = { "Walk", "Run", "Stand", "Eat", "Rear" };
        public static string PayStateName(Player p) { int i = PayState(p); return i >= 0 && i < PayStateNames.Length ? PayStateNames[i] : i.ToString(); }
        public static string ActionName(Player p) { int i = ActionState(p); return i >= 0 && i < ActionNames.Length ? ActionNames[i] : i.ToString(); }
    }
}
