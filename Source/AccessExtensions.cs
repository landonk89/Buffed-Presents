using BepInEx.Logging;
using System.Reflection;

namespace com.redcrowbar.buffedpresents
{
    static class AccessExtensions
    {
        public static ManualLogSource ExtLogger = Logger.CreateLogSource("AccessExtentions");

        //call nonpublic methods using reflection, ex. SomeClass.CallMethod("MethodInClass", param1, param2, ...);
        public static object CallMethod(this object o, string methodName, params object[] args)
        {
            var mi = o.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi != null)
            {
                ExtLogger.LogDebug($"Calling nonpublic method: {methodName}");
                return mi.Invoke(o, args);
            }
            ExtLogger.LogError($"Couldn't call nonpublic method: {methodName}");
            return null;
        }

        //Get nonpublic fields, ex. someVar = GetFieldValue<SomeClass>(someClassInstance, "someField");
        //returns Value of Type T contained in the nonprivate field
        public static T GetFieldValue<T>(object instance, string fieldName)
        {
            FieldInfo fieldInfo = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fieldInfo != null)
            {
                var retval = (T)fieldInfo.GetValue(instance);
                ExtLogger.LogDebug($"GetFieldValue: Field '{fieldName}' exists, returning {retval}");
                return retval;
            }

            ExtLogger.LogError($"GetFieldValue: Field '{fieldName}' not found!");
            return default;
        }

        //Set nonpublic fields, ex. SetFieldValue(someClassInstance, "fieldToSet", valueToSet);
        public static void SetFieldValue(object instance, string fieldName, object value)
        {
            FieldInfo fieldInfo = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

            if (fieldInfo != null)
            {
                fieldInfo.SetValue(instance, value);
                ExtLogger.LogDebug($"SetFieldValue: Field '{fieldName}' exists, setting {value}");
            }
            else
            {
                ExtLogger.LogError($"SetFieldValue: Field '{fieldName}' not found.");
            }
        }
    }
}
