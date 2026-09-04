package com.example.xpanel;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.TextView;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

public class AppPickerActivity extends Activity {

    static class AppItem {
        String pkg;
        String label;
        boolean checked;
    }

    private List<AppItem> items = new ArrayList<>();
    private AppAdapter adapter;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_app_picker);

        Set<String> selected = loadSelected();
        for (ApplicationInfo ai : getPackageManager().getInstalledApplications(PackageManager.GET_META_DATA)) {
            AppItem it = new AppItem();
            it.pkg = ai.packageName;
            it.label = getPackageManager().getApplicationLabel(ai).toString();
            if (ai.icon == 0) continue; // skip no-UI packages
            it.checked = selected.contains(it.pkg);
            items.add(it);
        }
        Collections.sort(items, (a, b) -> a.label.compareTo(b.label));

        ListView list = findViewById(R.id.listApps);
        adapter = new AppAdapter(this, items);
        list.setAdapter(adapter);

        Button btn = findViewById(R.id.btnDone);
        btn.setOnClickListener(v -> {
            JSONArray arr = new JSONArray();
            for (AppItem it : items) if (it.checked) arr.put(it.pkg);
            getSharedPreferences("cfg", 0).edit().putString("bypass_apps", arr.toString()).apply();
            finish();
        });
    }

    // ponytail: JSON array in SharedPreferences — simple, no dependency
    private Set<String> loadSelected() {
        String s = getSharedPreferences("cfg", 0).getString("bypass_apps", "");
        Set<String> set = new HashSet<>();
        try {
            JSONArray arr = new JSONArray(s);
            for (int i = 0; i < arr.length(); i++) set.add(arr.getString(i));
        } catch (JSONException ignored) {}
        return set;
    }

    static class AppAdapter extends ArrayAdapter<AppItem> {
        AppAdapter(Context c, List<AppItem> items) { super(c, 0, items); }

        @Override
        public View getView(int pos, View cv, ViewGroup parent) {
            if (cv == null) {
                cv = LayoutInflater.from(getContext()).inflate(R.layout.app_picker_item, parent, false);
            }
            AppItem it = getItem(pos);
            ((TextView) cv.findViewById(R.id.tvLabel)).setText(it.label);
            ((TextView) cv.findViewById(R.id.tvPkg)).setText(it.pkg);
            CheckBox cb = cv.findViewById(R.id.cbCheck);
            cb.setChecked(it.checked);
            cv.setOnClickListener(v -> {
                it.checked = !it.checked;
                cb.setChecked(it.checked);
            });
            return cv;
        }
    }
}
