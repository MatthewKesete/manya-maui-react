import os
import json
import pandas as pd
import re
from pathlib import Path
import sys

def detect_topic_from_content(json_data, filename):
    """Detect topic from JSON content and filename"""
    topic = json_data.get('topic', '')
    filename_lower = filename.lower()
    
    # First try to match from your 10 topics
    topic_map = {
        'finite': 'finite_vs_infinite_sets',
        'infinite': 'finite_vs_infinite_sets',
        'subset': 'calculating_subsets',
        'proper': 'calculating_proper_subsets',
        'venn': 'venn_diagram_placement',
        'union': 'set_notation_regions',
        'intersection': 'set_notation_regions',
        'probability': 'probability_venn_diagrams',
        'complement': 'difference_complements',
        'difference': 'difference_complements',
        'solve': 'solving_for_unknowns',
        'unknown': 'solving_for_unknowns',
        'application': 'application_of_sets',
        'backward': 'working_backwards',
        'classifier': 'finite_vs_infinite_sets',
        'game': 'calculating_subsets',
        'theory': 'solving_for_unknowns'
    }
    
    # Check topic from JSON
    if topic:
        topic_lower = topic.lower()
        for keyword, mapped_topic in topic_map.items():
            if keyword in topic_lower:
                return mapped_topic
    
    # Check filename
    for keyword, mapped_topic in topic_map.items():
        if keyword in filename_lower:
            return mapped_topic
    
    return 'general_sets'

def extract_difficulty(filepath):
    """Extract difficulty from path"""
    path_lower = filepath.lower()
    
    if any(word in path_lower for word in ['easy', 'e_', 'beginner', 'level1']):
        return 'E'
    elif any(word in path_lower for word in ['hard', 'h_', 'advanced', 'level3']):
        return 'H'
    elif any(word in path_lower for word in ['medium', 'm_', 'intermediate', 'level2']):
        return 'M'
    
    return 'M'  # Default to Medium

def create_q_id(file_path, base_folder):
    """Create unique Q_ID from file path"""
    # Get relative path from base folder
    rel_path = os.path.relpath(file_path, base_folder)
    
    # Clean up the path for Q_ID
    qid = rel_path.replace(os.sep, '-')  # Replace slashes with dashes
    qid = qid.replace('.json', '')      # Remove .json
    qid = qid.replace(' ', '_')         # Replace spaces
    qid = re.sub(r'[^a-zA-Z0-9_-]', '', qid)  # Remove special chars
    
    # Ensure it starts with SIM-
    if not qid.startswith('SIM-'):
        qid = 'SIM-' + qid
    
    # Shorten if too long
    if len(qid) > 50:
        qid = qid[:50]
    
    return qid

def crawl_simulations_to_excel(root_folder, output_file="simulations_bank.xlsx"):
    """Main function to crawl folders and create Excel file"""
    
    print(f"Starting to crawl: {root_folder}")
    print("Looking for JSON files...")
    
    data = []
    json_files_found = 0
    json_files_processed = 0
    
    # Walk through all folders
    for root, dirs, files in os.walk(root_folder):
        for file in files:
            if file.endswith('.json'):
                json_files_found += 1
                file_path = os.path.join(root, file)
                
                try:
                    with open(file_path, 'r', encoding='utf-8') as f:
                        sim_data = json.load(f)
                    
                    # Create Q_ID
                    q_id = create_q_id(file_path, root_folder)
                    
                    # Extract metadata
                    engine_type = sim_data.get('engineType', 'UNKNOWN')
                    mode = sim_data.get('mode', 'quiz')
                    json_topic = sim_data.get('topic', '')
                    
                    # Detect topic
                    topic = detect_topic_from_content(sim_data, file)
                    
                    # Extract difficulty
                    difficulty = extract_difficulty(file_path)
                    
                    # Count questions if available
                    questions = sim_data.get('questions', [])
                    question_count = len(questions) if isinstance(questions, list) else 1
                    
                    # Create row data
                    row_data = {
                        'Q_ID': q_id,
                        'Term': 'T1',  # Default term
                        'Topic': f"Sets – P7 (Uganda PLE)",
                        'Difficulty': difficulty,
                        'Question_Type': 'SIM',
                        'Parent_ID': '',
                        'Order_in_Parent': '',
                        'Question_Text': f"Interactive: {json_topic or topic.replace('_', ' ').title()}",
                        'Option_A': '',
                        'Option_B': '',
                        'Option_C': '',
                        'Option_D': '',
                        'Correct_Answer': '',
                        'Hint': 'Use the interactive simulation',
                        'Detailed_Solution': json.dumps({"type": "interactive_simulation"}),
                        'Engine_Type': engine_type,
                        'Mode': mode,
                        'Scene_Config': json.dumps(sim_data.get('sets', {}) if 'sets' in sim_data else {}),
                        'Interaction_Type': sim_data.get('questions', [{}])[0].get('type', 'INTERACTIVE') if questions else 'INTERACTIVE',
                        'Simulation_Data': json.dumps(sim_data, ensure_ascii=False),
                        'Image_Location': f"/simulations/{topic}/",
                        'Tags': json.dumps(["P7", "Sets", "Uganda PLE", "Interactive", "Simulation", engine_type]),
                        'Topics': topic,
                        'Subject': sim_data.get('subject', 'MATH'),
                        'Variant_Title': sim_data.get('variantTitle', ''),
                        'Question_Count': question_count,
                        'File_Path': file_path,
                        'Folder': os.path.dirname(file_path),
                        'Filename': file
                    }
                    
                    data.append(row_data)
                    json_files_processed += 1
                    
                    print(f"✓ Processed: {file}")
                    
                except json.JSONDecodeError as e:
                    print(f"✗ Error parsing JSON in {file}: {e}")
                except Exception as e:
                    print(f"✗ Error reading {file}: {e}")
    
    print(f"\nFound {json_files_found} JSON files")
    print(f"Successfully processed {json_files_processed} files")
    
    if not data:
        print("No data to save!")
        return None
    
    # Create DataFrame
    df = pd.DataFrame(data)
    
    # Define column order (matching your existing format)
    column_order = [
        'Q_ID', 'Term', 'Topic', 'Difficulty', 'Question_Type',
        'Parent_ID', 'Order_in_Parent', 'Question_Text',
        'Option_A', 'Option_B', 'Option_C', 'Option_D',
        'Correct_Answer', 'Hint', 'Detailed_Solution',
        'Engine_Type', 'Mode', 'Scene_Config', 'Interaction_Type',
        'Simulation_Data', 'Image_Location', 'Tags', 'Topics',
        'Subject', 'Variant_Title', 'Question_Count',
        'File_Path', 'Folder', 'Filename'
    ]
    
    # Reorder columns
    df = df.reindex(columns=column_order)
    
    # Save to Excel
    try:
        df.to_excel(output_file, index=False)
        print(f"\n✅ Successfully saved to: {output_file}")
        print(f"📊 Total records: {len(df)}")
        
        # Show sample data
        print("\n📋 Sample of first 3 records:")
        print(df[['Q_ID', 'Engine_Type', 'Topics', 'Difficulty']].head(3).to_string())
        
    except Exception as e:
        print(f"✗ Error saving to Excel: {e}")
        # Try CSV as fallback
        try:
            csv_file = output_file.replace('.xlsx', '.csv')
            df.to_csv(csv_file, index=False, encoding='utf-8')
            print(f"✅ Saved to CSV instead: {csv_file}")
        except Exception as e2:
            print(f"✗ Also failed to save CSV: {e2}")
    
    return df

def main():
    """Main execution function"""
    print("=" * 60)
    print("SIMULATION DATA CRAWLER")
    print("=" * 60)
    
    # Get folder path from user or use current directory
    if len(sys.argv) > 1:
        root_folder = sys.argv[1]
    else:
        root_folder = input("Enter path to simulations folder (or press Enter for current directory): ").strip()
        if not root_folder:
            root_folder = "."
    
    # Verify folder exists
    if not os.path.exists(root_folder):
        print(f"Error: Folder '{root_folder}' does not exist!")
        return
    
    # Get output filename
    output_file = input("Enter output filename [default: simulations_bank.xlsx]: ").strip()
    if not output_file:
        output_file = "simulations_bank.xlsx"
    
    # Ensure .xlsx extension
    if not output_file.endswith('.xlsx'):
        output_file += '.xlsx'
    
    print(f"\n📁 Scanning folder: {os.path.abspath(root_folder)}")
    print(f"💾 Output file: {output_file}")
    print("-" * 60)
    
    # Run the crawler
    df = crawl_simulations_to_excel(root_folder, output_file)
    
    if df is not None:
        print("\n" + "=" * 60)
        print("NEXT STEPS:")
        print("1. Open the Excel file to review the data")
        print("2. You can merge this with your existing question bank")
        print("3. Use filters to sort by Topics, Engine_Type, etc.")
        print("=" * 60)

if __name__ == "__main__":
    main()